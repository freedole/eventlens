using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using EventLens.Core.Util;

namespace EventLens.Controls;

/// <summary>Turns the small Markdown subset produced by AI models into a WPF FlowDocument.</summary>
public static class MarkdownRenderer
{
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    public static FlowDocument Build(string markdown)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 14,
            TextAlignment = TextAlignment.Left,
        };
        doc.SetResourceReference(FlowDocument.ForegroundProperty, "TextFillColorPrimaryBrush");

        List? currentList = null;
        MdBlockKind? currentKind = null;

        foreach (var b in Markdown.Parse(markdown))
        {
            if (b.Kind is MdBlockKind.Bullet or MdBlockKind.Numbered)
            {
                if (currentList is null || currentKind != b.Kind)
                {
                    currentList = new List
                    {
                        MarkerStyle = b.Kind == MdBlockKind.Bullet ? TextMarkerStyle.Disc : TextMarkerStyle.Decimal,
                        Margin = new Thickness(0, 2, 0, 8),
                        Padding = new Thickness(20, 0, 0, 0),
                    };
                    if (b.Kind == MdBlockKind.Numbered && b.Number > 1) currentList.StartIndex = b.Number;
                    doc.Blocks.Add(currentList);
                    currentKind = b.Kind;
                }
                var p = MakeParagraph(b.Inlines);
                p.Margin = new Thickness(b.Level * 16, 1, 0, 1);
                currentList.ListItems.Add(new ListItem(p));
                continue;
            }

            currentList = null;
            currentKind = null;

            switch (b.Kind)
            {
                case MdBlockKind.Heading:
                {
                    var p = MakeParagraph(b.Inlines);
                    p.FontWeight = FontWeights.SemiBold;
                    p.FontSize = b.Level <= 2 ? 17 : 15;
                    p.Margin = new Thickness(0, 10, 0, 4);
                    doc.Blocks.Add(p);
                    break;
                }
                case MdBlockKind.Code:
                {
                    var p = new Paragraph(new Run(b.Raw))
                    {
                        FontFamily = Mono,
                        FontSize = 12.5,
                        Padding = new Thickness(10, 8, 10, 8),
                        Margin = new Thickness(0, 4, 0, 8),
                    };
                    p.SetResourceReference(Block.BackgroundProperty, "CardBackgroundFillColorSecondaryBrush");
                    doc.Blocks.Add(p);
                    break;
                }
                case MdBlockKind.Rule:
                    doc.Blocks.Add(new BlockUIContainer(new System.Windows.Controls.Separator { Margin = new Thickness(0, 6, 0, 6) }));
                    break;
                default:
                {
                    var p = MakeParagraph(b.Inlines);
                    p.Margin = new Thickness(0, 0, 0, 8);
                    doc.Blocks.Add(p);
                    break;
                }
            }
        }
        return doc;
    }

    private static Paragraph MakeParagraph(IEnumerable<MdInline> inlines)
    {
        var p = new Paragraph();
        foreach (var i in inlines)
        {
            var run = new Run(i.Text);
            if (i.Code)
            {
                run.FontFamily = Mono;
                run.FontSize = 12.5;
                run.SetResourceReference(TextElement.BackgroundProperty, "CardBackgroundFillColorSecondaryBrush");
            }
            if (i.Bold) run.FontWeight = FontWeights.SemiBold;
            if (i.Italic) run.FontStyle = FontStyles.Italic;
            p.Inlines.Add(run);
        }
        return p;
    }
}
