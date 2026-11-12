// Turns uploaded files into plain text: .txt/.md/.csv directly, .docx and .xlsx with Open XML (PDF goes through Claude).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace RiwaqChat
{
    public static class Extract
    {
        public static string TypeOf(string fileName)
        {
            switch (Path.GetExtension(fileName ?? "").ToLowerInvariant())
            {
                case ".txt": case ".md": case ".csv": return "txt";
                case ".pdf": return "pdf";
                case ".docx": return "docx";
                case ".xlsx": return "xlsx";
                default: return null;
            }
        }

        public static string PlainText(byte[] data)
        {
            // UTF-8 (with or without BOM); fall back to Windows-1256 (Arabic) for old files
            try { return new UTF8Encoding(false, true).GetString(data).TrimStart('﻿'); }
            catch { return Encoding.GetEncoding(1256).GetString(data); }
        }

        public static string Word(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var doc = WordprocessingDocument.Open(ms, false))
            {
                var body = doc.MainDocumentPart?.Document?.Body;
                if (body == null) return "";
                var sb = new StringBuilder();
                foreach (var el in body.ChildElements)
                {
                    if (el is W.Paragraph p)
                    {
                        var t = p.InnerText.Trim();
                        if (t.Length > 0) sb.AppendLine(t);
                    }
                    else if (el is W.Table tbl)
                    {
                        foreach (var row in tbl.Elements<W.TableRow>())
                        {
                            var cells = row.Elements<W.TableCell>().Select(c => c.InnerText.Trim());
                            sb.AppendLine(string.Join(" | ", cells));
                        }
                        sb.AppendLine();
                    }
                }
                return sb.ToString().Trim();
            }
        }

        public static string Excel(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var doc = SpreadsheetDocument.Open(ms, false))
            {
                var wb = doc.WorkbookPart;
                var shared = wb.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().Select(s => s.InnerText).ToList() ?? new List<string>();
                var sb = new StringBuilder();
                foreach (var sheet in wb.Workbook.Sheets.Elements<Sheet>())
                {
                    var part = (WorksheetPart)wb.GetPartById(sheet.Id);
                    var rows = part.Worksheet.Descendants<Row>().ToList();
                    if (rows.Count == 0) continue;
                    sb.AppendLine("## " + sheet.Name);
                    List<string> header = null;
                    foreach (var row in rows)
                    {
                        var values = new SortedDictionary<int, string>();
                        foreach (var cell in row.Elements<Cell>())
                        {
                            var v = CellText(cell, shared);
                            if (!string.IsNullOrWhiteSpace(v)) values[ColIndex(cell.CellReference)] = v.Trim();
                        }
                        if (values.Count == 0) continue;
                        if (header == null)
                        {
                            int max = values.Keys.Max();
                            header = Enumerable.Range(0, max + 1).Select(i => values.ContainsKey(i) ? values[i] : "عمود " + (i + 1)).ToList();
                            continue;
                        }
                        sb.AppendLine(string.Join(" | ", values.Select(kv => (kv.Key < header.Count ? header[kv.Key] : "عمود " + (kv.Key + 1)) + ": " + kv.Value)));
                    }
                    if (header != null && sb.Length > 0 && rows.Count == 1) sb.AppendLine(string.Join(" | ", header));
                    sb.AppendLine();
                }
                return sb.ToString().Trim();
            }
        }

        static string CellText(Cell cell, List<string> shared)
        {
            if (cell == null) return null;
            if (cell.DataType != null && cell.DataType.Value == CellValues.SharedString)
            {
                int i;
                return int.TryParse(cell.CellValue?.Text, out i) && i >= 0 && i < shared.Count ? shared[i] : null;
            }
            if (cell.DataType != null && cell.DataType.Value == CellValues.InlineString) return cell.InlineString?.InnerText;
            return cell.CellValue?.Text;
        }

        static int ColIndex(string reference)
        {
            if (string.IsNullOrEmpty(reference)) return 0;
            int n = 0;
            foreach (char ch in reference)
            {
                if (ch < 'A' || ch > 'Z') break;
                n = n * 26 + (ch - 'A' + 1);
            }
            return Math.Max(0, n - 1);
        }
    }
}
