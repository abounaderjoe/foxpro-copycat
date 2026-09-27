# Report and label format (`.jpreport`, `.jplabel`)

Reports and labels are stored as a strict subset of YAML: block mappings and lists, plain or quoted strings,
literal blocks (`|`) for code, and comments. There are no anchors, tags or flow collections. The file is
canonical: keys come in a fixed order, only values that differ from the defaults are written, and measurements
are in inches (rounded to 1/10000). Changing one property changes one line.

```yaml
# Joe Pro report v1
description: Orders by customer
page:
  paper: A4                 # Letter (default), Legal, A4, A5, A3, Executive, Custom
  width: 8.2677             # paper size in portrait orientation
  height: 11.6929
  orientation: landscape    # portrait (default)
  margins:
    left: 0.5               # all margins default to 0.5
  columns:
    count: 2
    width: 3.5              # 0 = divide the printable width
    spacing: 0.25
    order: across           # down (reports) / across (labels)
font:
  name: Arial               # default font for text objects
  size: 10
titleOnNewPage: true
summaryOnNewPage: true
privateDataSession: true
dataEnvironment: |
  DEFINE CLASS ReportDataEnvironment AS DataEnvironment
      ADD OBJECT Cursor1 AS Cursor WITH ;
          Alias = "orders", ;
          CursorSource = "orders.jpt", ;
          Order = "cust"
      PROCEDURE BeforeOpenTables
          SET DELETED ON
      ENDPROC
  ENDDEFINE
variables:
  - name: nCount
    value: "1"
    calculate: count        # none, count, sum, average, lowest, highest, stdDev, variance
    reset: group 1          # report (default), page, column, group N
groups:                     # outermost first
  - expression: orders.cust
    newPage: true
    newColumn: false
    resetPageNumber: false
    reprintHeader: true
    minSpace: 1.5           # start on a new page when less space is left
bands:
  - band: pageHeader        # title, pageHeader, columnHeader, groupHeader, detailHeader,
    height: 0.5             # detail, detailFooter, groupFooter, columnFooter, pageFooter, summary
    objects:
      - label: Orders
        left: 0
        top: 0
        width: 2
        height: 0.3
        font:
          size: 16
          bold: true
        foreColor: 31,78,121
  - band: groupHeader
    index: 1                # group number (detail bands: 1 by default)
    height: 0.25
    onEntry: "nPrinted = 0" # expressions run when the band starts and ends
    objects:
      - field: orders.cust
        left: 0
        top: 0
        width: 3
        height: 0.2
  - band: detail
    height: 0.2
    targetAlias: lines      # VFP 9 detail band driven by a related child table
    objects:
      - field: orders.amount
        left: 5
        top: 0
        width: 1.2
        height: 0.2
        format: 99,999.99   # TRANSFORM picture/format
        align: right        # left (default), center, right
        printWhen: orders.amount <> 0
        printRepeated: false
        stretch: true       # grow to show all the text
        float: float        # top (default), float, bottom
        removeLineIfBlank: true
      - line: horizontal    # horizontal or vertical
        left: 0
        top: 0.19
        width: 7.5
        height: 0
        penWidth: 0.5       # points
        penStyle: dot       # solid, dot, dash, dashDot, dashDotDot, none
        color: 128,128,128
      - shape: rounded      # rectangle, rounded (with curvature 1-98), ellipse
        curvature: 20
        fillColor: 230,238,245
        lineColor: 0,0,0
      - picture: logo.png
        source: file        # file, field (General field), expression
        scale: scale        # clip, scale (keep shape), stretch
  - band: groupFooter
    index: 1
    height: 0.3
    objects:
      - field: orders.amount
        calculate: sum
        reset: group 1
```

Colors are `red,green,blue`. Expressions are FoxPro expressions evaluated when the report runs, in the data
session of the report; report variables are memory variables while it runs (released afterwards unless
`release: false`). Labels (`.jplabel`) use the same format; their header is `# Joe Pro label v1` and their columns
print across by default.
