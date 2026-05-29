# python-docx Pattern Library

## Install

```bash
pip install python-docx
```

## New document

```python
from docx import Document
doc = Document()
doc.save("output.docx")
```

## Clone a template (preserves styles, headers, footers)

```python
doc = Document("template.docx")
# Clear body content while keeping styles
for elem in list(doc.element.body):
    doc.element.body.remove(elem)
```

## Title and headings

```python
doc.add_heading("Document Title", level=0)   # Title style
doc.add_heading("Chapter One", level=1)       # Heading 1
doc.add_heading("Section 1.1", level=2)       # Heading 2
doc.add_heading("Sub-section", level=3)       # Heading 3
```

## Paragraphs

```python
doc.add_paragraph("Body text here.")

# Bold / italic inline
from docx.util import Pt
p = doc.add_paragraph()
run = p.add_run("Bold text")
run.bold = True
run2 = p.add_run(" and normal text.")
```

## Bulleted and numbered lists

```python
doc.add_paragraph("First item", style="List Bullet")
doc.add_paragraph("Second item", style="List Bullet")

doc.add_paragraph("Step one", style="List Number")
doc.add_paragraph("Step two", style="List Number")
```

## Tables

```python
table = doc.add_table(rows=1, cols=3)
table.style = "Table Grid"

# Header row
hdr = table.rows[0].cells
hdr[0].text = "Column A"
hdr[1].text = "Column B"
hdr[2].text = "Column C"

# Data rows
for name, value, status in data:
    row = table.add_row().cells
    row[0].text = name
    row[1].text = str(value)
    row[2].text = status
```

## Images

```python
from docx.shared import Inches
doc.add_picture("path/to/image.png", width=Inches(4))
```

## Code blocks (monospace paragraph)

```python
from docx.shared import Pt
from docx.enum.text import WD_COLOR_INDEX

p = doc.add_paragraph()
p.style = doc.styles["Normal"]
run = p.add_run("def hello(): pass")
run.font.name = "Courier New"
run.font.size = Pt(9)
```

## Page break

```python
doc.add_page_break()
```

## Horizontal rule (paragraph border)

```python
from docx.oxml.ns import qn
from docx.oxml import OxmlElement

p = doc.add_paragraph()
pPr = p._p.get_or_add_pPr()
pBdr = OxmlElement("w:pBdr")
bottom = OxmlElement("w:bottom")
bottom.set(qn("w:val"), "single")
bottom.set(qn("w:sz"), "6")
bottom.set(qn("w:space"), "1")
bottom.set(qn("w:color"), "auto")
pBdr.append(bottom)
pPr.append(pBdr)
```

## Template placeholder substitution

```python
import re

def replace_placeholders(doc, variables: dict):
    pattern = re.compile(r"\{\{(\w+)\}\}")
    for para in doc.paragraphs:
        for run in para.runs:
            def replacer(m):
                return variables.get(m.group(1), m.group(0))
            run.text = pattern.sub(replacer, run.text)
    for table in doc.tables:
        for row in table.rows:
            for cell in row.cells:
                for para in cell.paragraphs:
                    for run in para.runs:
                        run.text = pattern.sub(
                            lambda m: variables.get(m.group(1), m.group(0)),
                            run.text
                        )
```

## Save

```python
import os
os.makedirs(os.path.dirname(output_path), exist_ok=True)
doc.save(output_path)
print(f"Saved: {output_path}")
```
