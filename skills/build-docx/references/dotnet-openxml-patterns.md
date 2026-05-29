# DocumentFormat.OpenXml Pattern Library

## Install

```bash
dotnet add package DocumentFormat.OpenXml
# or for simpler API (Word only):
dotnet add package DocX
```

## Minimal document (OpenXml SDK)

```csharp
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

using var doc = WordprocessingDocument.Create("output.docx", WordprocessingDocumentType.Document);
var mainPart = doc.AddMainDocumentPart();
mainPart.Document = new Document(new Body(new Paragraph(new Run(new Text("Hello, world.")))));
mainPart.Document.Save();
```

## Minimal document (DocX — simpler API)

```csharp
using Xceed.Words.NET;

using var doc = DocX.Create("output.docx");
doc.InsertParagraph("Hello, world.");
doc.Save();
```

## Headings (DocX)

```csharp
doc.InsertParagraph("Title").StyleId("Title");
doc.InsertParagraph("Chapter One").StyleId("Heading1");
doc.InsertParagraph("Section 1.1").StyleId("Heading2");
doc.InsertParagraph("Sub-section").StyleId("Heading3");
```

## Paragraphs with inline formatting (DocX)

```csharp
var p = doc.InsertParagraph();
p.Append("Bold text").Bold();
p.Append(" and normal text.");
p.Append(" Italic").Italic();
```

## Bulleted and numbered lists (DocX)

```csharp
doc.InsertParagraph("First item").StyleId("ListBullet");
doc.InsertParagraph("Second item").StyleId("ListBullet");

doc.InsertParagraph("Step one").StyleId("ListNumber");
doc.InsertParagraph("Step two").StyleId("ListNumber");
```

## Tables (DocX)

```csharp
var table = doc.InsertTable(1, 3);
table.Rows[0].Cells[0].Paragraphs[0].Append("Column A");
table.Rows[0].Cells[1].Paragraphs[0].Append("Column B");
table.Rows[0].Cells[2].Paragraphs[0].Append("Column C");

foreach (var (name, value, status) in data)
{
    var row = table.InsertRow();
    row.Cells[0].Paragraphs[0].Append(name);
    row.Cells[1].Paragraphs[0].Append(value.ToString());
    row.Cells[2].Paragraphs[0].Append(status);
}
```

## Images (DocX)

```csharp
using var img = doc.AddImage("path/to/image.png");
var picture = img.CreatePicture(100, 150); // height, width in points
doc.InsertParagraph().AppendPicture(picture);
```

## Code block (monospace paragraph, DocX)

```csharp
doc.InsertParagraph("var x = 42;")
   .Font(new Xceed.Document.NET.Font("Courier New"))
   .FontSize(9);
```

## Page break (DocX)

```csharp
doc.InsertParagraph().InsertPageBreakAfterSelf();
```

## Template placeholder substitution (DocX)

```csharp
doc.ReplaceText("{{Name}}", "Alice");
doc.ReplaceText("{{Date}}", DateTime.Today.ToString("yyyy-MM-dd"));
```

For bulk substitution from a dictionary:

```csharp
foreach (var (key, value) in variables)
    doc.ReplaceText($"{{{{{key}}}}}", value);
```

## Save (DocX)

```csharp
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
doc.SaveAs(outputPath);
Console.WriteLine($"Saved: {outputPath}");
```
