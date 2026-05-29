# docx (npm) Pattern Library

## Install

```bash
npm install docx
```

## Minimal document

```typescript
import { Document, Packer, Paragraph } from "docx";
import fs from "fs";

const doc = new Document({
  sections: [{ children: [new Paragraph("Hello, world.")] }],
});

Packer.toBuffer(doc).then((buf) => fs.writeFileSync("output.docx", buf));
```

## Headings

```typescript
import { HeadingLevel } from "docx";

new Paragraph({ text: "Title", heading: HeadingLevel.TITLE }),
new Paragraph({ text: "Chapter One", heading: HeadingLevel.HEADING_1 }),
new Paragraph({ text: "Section 1.1", heading: HeadingLevel.HEADING_2 }),
new Paragraph({ text: "Sub-section", heading: HeadingLevel.HEADING_3 }),
```

## Inline runs (bold, italic, font size)

```typescript
import { TextRun } from "docx";

new Paragraph({
  children: [
    new TextRun({ text: "Bold text", bold: true }),
    new TextRun(" and normal text."),
    new TextRun({ text: "Italic", italics: true }),
  ],
})
```

## Bulleted and numbered lists

```typescript
import { LevelFormat } from "docx";

// Bullet
new Paragraph({ text: "First item", bullet: { level: 0 } }),

// Numbered — requires a numbering config in the Document constructor
new Paragraph({
  text: "Step one",
  numbering: { reference: "my-numbering", level: 0 },
}),
```

For numbered lists, add a `numbering` block to `Document`:

```typescript
new Document({
  numbering: {
    config: [{
      reference: "my-numbering",
      levels: [{ level: 0, format: LevelFormat.DECIMAL, text: "%1.", alignment: "left" }],
    }],
  },
  sections: [...],
})
```

## Tables

```typescript
import { Table, TableRow, TableCell, WidthType } from "docx";

new Table({
  width: { size: 100, type: WidthType.PERCENTAGE },
  rows: [
    new TableRow({
      children: [
        new TableCell({ children: [new Paragraph("Column A")] }),
        new TableCell({ children: [new Paragraph("Column B")] }),
      ],
    }),
    ...dataRows.map(([a, b]) =>
      new TableRow({
        children: [
          new TableCell({ children: [new Paragraph(a)] }),
          new TableCell({ children: [new Paragraph(b)] }),
        ],
      })
    ),
  ],
})
```

## Images

```typescript
import { ImageRun } from "docx";
import fs from "fs";

new Paragraph({
  children: [
    new ImageRun({
      data: fs.readFileSync("path/to/image.png"),
      transformation: { width: 400, height: 300 },
    }),
  ],
})
```

## Code block (monospace)

```typescript
import { UnderlineType } from "docx";

new Paragraph({
  children: [
    new TextRun({
      text: "const x = 42;",
      font: "Courier New",
      size: 18, // half-points
    }),
  ],
})
```

## Page break

```typescript
import { PageBreak } from "docx";

new Paragraph({ children: [new PageBreak()] })
```

## Save (Node.js)

```typescript
import { Packer } from "docx";
import fs from "fs";
import path from "path";

const outPath = path.resolve("output/document.docx");
fs.mkdirSync(path.dirname(outPath), { recursive: true });
Packer.toBuffer(doc).then((buf) => {
  fs.writeFileSync(outPath, buf);
  console.log(`Saved: ${outPath}`);
});
```
