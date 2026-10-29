--[[
  onenote-clean.lua — turn a OneNote page (Graph "Get page content" HTML) into plain markdown.

  Why this exists
  ---------------
  Written for a team's weekly updates kept in a shared OneNote notebook, where a Power
  Automate flow saves each page as HTML into the staging folder. OneNote expresses all formatting as inline
  styles: every run of text is a <span style="...">, the page is wrapped in an
  absolute-positioned <div>, and blank lines are <br/>. Converted as-is, the markdown is
  tag soup with a backslash line break on every other line.

  What it does
  ------------
  1. Keeps bold, the only styling that carries meaning in these pages (section names and
     item headlines), and drops every other span, keeping its text.
  2. Unwraps the layout <div>.
  3. Turns line breaks into spaces and drops paragraphs left empty.
  4. Joins adjacent bold runs. OneNote splits a word across spans when it is edited
     mid-word, so "E" + "DW query skill" would otherwise come out as **E****DW query skill**.
  5. Replaces U+FFFC, the "object replacement character", with a space. Apps leave it
     behind when text containing an inline object is pasted. OneNote does not display it,
     and the page has no object behind it — checked on 5 Oct 2026, the HTML carried no
     <img> or <object> at all.
  6. Unwraps superscript, so ordinals read "10th" rather than "10<sup>th</sup>".

  It needs the html reader with native spans ON ("inputFormat": "html" on the rule). The
  default html format in doc-filer switches them off, which strips the bold before this
  filter ever sees it.

  Content-preserving: no text is discarded, only styling.
]]

local OBJECT_REPLACEMENT = "\239\191\188" -- U+FFFC in UTF-8

local function is_bold(attr)
  local style = attr.attributes and attr.attributes.style or ""
  return style:find("font%-weight:%s*bold") ~= nil
end

function Div(el) return el.content end

function Span(el)
  if is_bold(el.attr) then return pandoc.Strong(el.content) end
  return el.content
end

function LineBreak() return pandoc.Space() end

-- U+FFFC can sit between two words with no space either side ("delivered￼Phoenix" in the
-- 25 May 2026 page), so it is replaced with a space, not deleted. Deleting it glued words
-- together, which the hand-versus-fresh word comparison caught on the first backfill.
function Str(el)
  if not el.text:find(OBJECT_REPLACEMENT, 1, true) then return nil end
  local out = {}
  for piece in (el.text .. OBJECT_REPLACEMENT):gmatch("(.-)" .. OBJECT_REPLACEMENT) do
    if piece ~= "" then table.insert(out, pandoc.Str(piece)) end
    table.insert(out, pandoc.Space())
  end
  table.remove(out) -- the separator after the last piece
  return out
end

-- "10<sup>th</sup>" is an ordinal, not a footnote; keep it as plain "10th".
function Superscript(el) return el.content end

function Inlines(inlines)
  local out = {}
  for _, inline in ipairs(inlines) do
    local previous = out[#out]
    if inline.t == "Strong" and previous and previous.t == "Strong" then
      for _, child in ipairs(inline.content) do previous.content:insert(child) end
    else
      table.insert(out, inline)
    end
  end
  return out
end

-- The flow's saved file has opened with a stray "/" before <html>, which pandoc reads as a
-- paragraph; treat it as empty along with the genuinely blank ones.
local function is_empty(block)
  if block.t ~= "Para" and block.t ~= "Plain" then return false end
  local text = pandoc.utils.stringify(block):gsub("%s", "")
  return text == "" or text == "/"
end

function Blocks(blocks)
  local out = {}
  for _, block in ipairs(blocks) do
    if not is_empty(block) then table.insert(out, block) end
  end
  return out
end
