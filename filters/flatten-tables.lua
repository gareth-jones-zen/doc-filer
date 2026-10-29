--[[
  flatten-tables.lua — make Word/HTML form tables expressible as markdown pipe tables.

  Why this exists
  ---------------
  A typical HR form, such as a performance review, is built out of Word tables used for *layout*, not
  for data: boxed sections, label/value pairs, and one rating grid. Pandoc cannot write
  a table as pipes if any cell is merged or holds more than one block, so it falls back
  to raw HTML — six <table> blocks in a single form, which makes the markdown source
  unreadable even though it previews fine.

  What it does, in order
  ----------------------
  1. Expands merged cells so every row has the same number of single-span cells.
  2. Flattens each cell to one line, joining paragraphs and list items (at any nesting
     depth) with a bullet, so nothing is dropped. A line break cannot be used: one
     anywhere in a table sends the whole table back to raw HTML.
  3. Drops columns that are empty in every row — the review form's rating grid declares
     ten columns and uses four.
  4. Unwraps anything left one column wide: a one-column table is a box, not a table,
     so its content is emitted as ordinary blocks with the header row as a heading.

  It is deliberately content-preserving. Layout is lost — merged cells become repeated
  values, and the rating grid's column widths go — but no text is discarded, which is
  the property that matters for a personnel record.
]]

local utils = pandoc.utils

-- Parts of a flattened cell are joined with a bullet rather than a line break. A GFM
-- pipe table cell cannot contain a line break, and a single LineBreak anywhere in the
-- table sends the whole thing back to raw HTML — which is the problem being solved.
local SEPARATOR = pandoc.Str("\u{2022}")

--- Images become their alt text.
--- The review form's rating columns are smiley SVGs whose alt text ("Sad face...",
--- "Grinning face...") is the only thing that says what the column means. Left as
--- images they would point at media files that are never copied, and the prettifier
--- would then strip them, taking the meaning with them.
function Image(image)
  local alt = utils.stringify(image.caption)
  if alt == "" then return {} end
  return pandoc.Str(alt)
end

--- Superscript and subscript become plain text.
--- GFM has no syntax for either, so pandoc writes raw <sup>/<sub> — six of the review
--- forms carried "3<sup>rd</sup> party". Unwrapping them reads correctly ("3rd party"),
--- keeps the word searchable, and removes the last raw HTML from the output.
function Superscript(element) return element.content end
function Subscript(element) return element.content end

--- Flatten a cell's blocks into a single line of inlines, joined with a bullet.
---
--- Recursive on purpose. pandoc.utils.blocks_to_inlines silently DROPS a list nested
--- inside a list item. On a real form that threw away the sub-bullets holding someone's
--- delivery figures while keeping the sentence that introduces them, so the cell still
--- read plausibly. A filter that
--- quietly loses the evidence in a personnel record is worse than no filter, so every
--- list is walked by hand, at every depth.
local function flatten_cell_contents(blocks)
  local inlines = {}

  local function append(part)
    if part == nil or #part == 0 then return end
    if #inlines > 0 then
      table.insert(inlines, pandoc.Space())
      table.insert(inlines, SEPARATOR)
      table.insert(inlines, pandoc.Space())
    end
    for _, inline in ipairs(part) do table.insert(inlines, inline) end
  end

  local function walk(block_list)
    for _, block in ipairs(block_list) do
      if block.t == "BulletList" or block.t == "OrderedList" then
        for _, item in ipairs(block.content) do walk(item) end
      elseif block.t == "DefinitionList" then
        for _, entry in ipairs(block.content) do
          append(entry[1])
          for _, definition in ipairs(entry[2]) do walk(definition) end
        end
      elseif block.t == "BlockQuote" or block.t == "Div" then
        walk(block.content)
      elseif block.t == "Para" or block.t == "Plain" or block.t == "Header" then
        append(block.content)
      else
        append(utils.blocks_to_inlines({ block }))
      end
    end
  end

  walk(blocks)

  -- A single line break anywhere in a table is enough to force the writer back to raw
  -- HTML, and Word cells contain them. Hard breaks become the separator; soft breaks
  -- were only source wrapping, so they become a space.
  --
  -- Walked rather than iterated, because Word puts breaks INSIDE the formatting: a
  -- heading cell reading "**Alt-nets and CSI<br><br>**" keeps its breaks inside the
  -- Strong, where a flat pass over the top-level list never sees them. That left two
  -- of the thirty-one forms silently still in raw HTML.
  local walked = pandoc.Span(inlines):walk({
    LineBreak = function() return { pandoc.Space(), SEPARATOR, pandoc.Space() } end,
    SoftBreak = function() return pandoc.Space() end
  })
  return walked.content
end

local function new_cell(inlines)
  return pandoc.Cell({ pandoc.Plain(inlines) }, pandoc.AlignDefault, 1, 1)
end

--- Expand merged cells and flatten contents.
--- Each cell becomes { inl = flattened inlines, blk = the original blocks }. The original
--- blocks are kept because a one-column table is unwrapped back into the document, and
--- there the paragraph structure of a long written answer must survive — flattening a
--- five-paragraph self-assessment into one bulleted run is exactly what not to do.
local function normalise_rows(rows)
  local out = {}
  for _, row in ipairs(rows) do
    local cells = {}
    for _, cell in ipairs(row.cells) do
      table.insert(cells, { inl = flatten_cell_contents(cell.contents), blk = cell.contents })
      -- A colspan becomes the value plus blanks, so column positions still line up.
      for _ = 2, (cell.col_span or 1) do
        table.insert(cells, { inl = {}, blk = {} })
      end
    end
    table.insert(out, cells)
  end
  return out
end

local function width_of(rows)
  local width = 0
  for _, cells in ipairs(rows) do
    if #cells > width then width = #cells end
  end
  return width
end

local function is_blank(inlines)
  return utils.stringify(pandoc.Plain(inlines)):gsub("%s+", "") == ""
end

--- Indexes of columns that hold something in at least one row.
local function used_columns(rows, width)
  local keep = {}
  for column = 1, width do
    for _, cells in ipairs(rows) do
      if cells[column] and not is_blank(cells[column].inl) then
        table.insert(keep, column)
        break
      end
    end
  end
  return keep
end

function Table(tbl)
  local head_rows = normalise_rows(tbl.head.rows)
  local body_rows = {}
  -- A TableBody exposes .head (intermediate header rows) and .body (the rest) — it has
  -- no .rows, which is the easy mistake here because TableHead and TableFoot do.
  for _, body in ipairs(tbl.bodies) do
    for _, cells in ipairs(normalise_rows(body.head)) do
      table.insert(body_rows, cells)
    end
    for _, cells in ipairs(normalise_rows(body.body)) do
      table.insert(body_rows, cells)
    end
  end
  for _, cells in ipairs(normalise_rows(tbl.foot.rows)) do
    table.insert(body_rows, cells)
  end

  local all = {}
  for _, r in ipairs(head_rows) do table.insert(all, r) end
  for _, r in ipairs(body_rows) do table.insert(all, r) end
  if #all == 0 then return tbl end

  local keep = used_columns(all, width_of(all))
  if #keep == 0 then return {} end

  -- One column means this was a layout box. Emit its contents as ordinary blocks:
  -- a table with a single column reads badly and carries no tabular meaning.
  if #keep == 1 then
    local blocks = {}
    for index, cells in ipairs(all) do
      local cell = cells[keep[1]] or { inl = {}, blk = {} }
      if not is_blank(cell.inl) then
        -- The first row of a one-column box is its label, so it becomes a heading.
        if index == 1 and #head_rows > 0 then
          table.insert(blocks, pandoc.Header(3, cell.inl))
        else
          -- Original blocks, not the flattened line: paragraphs and lists stay intact.
          for _, block in ipairs(cell.blk) do table.insert(blocks, block) end
        end
      end
    end
    return blocks
  end

  local function rebuild(rows)
    local built = {}
    for _, cells in ipairs(rows) do
      local picked = {}
      for _, column in ipairs(keep) do
        table.insert(picked, new_cell((cells[column] or {}).inl or {}))
      end
      table.insert(built, pandoc.Row(picked))
    end
    return built
  end

  local head = pandoc.TableHead(rebuild(head_rows))

  -- There is no pandoc.TableBody constructor, so the existing body is reused and its
  -- rows replaced. row_head_columns is cleared: the review form marks leading columns
  -- as row headers, which the pipe writer cannot express.
  local body = tbl.bodies[1]
  body.head = {}
  body.body = rebuild(body_rows)
  body.row_head_columns = 0

  -- Column widths are dropped (AlignDefault, ColWidthDefault) so the writer sizes the
  -- pipe table to its content rather than reproducing Word's percentages.
  local colspecs = {}
  for _ = 1, #keep do
    table.insert(colspecs, { pandoc.AlignDefault, nil })
  end

  return pandoc.Table(tbl.caption, colspecs, head, { body }, pandoc.TableFoot())
end
