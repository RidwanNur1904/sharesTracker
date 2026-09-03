using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using YahooFinanceApi;

using IOPath = System.IO.Path;

namespace sharesTracker;

public partial class SupplyChain : Window
{
    // ================================================================
    // ZOOM
    // ================================================================

    private double _zoomFactor = 1.0;

    private readonly ScaleTransform _scaleTransform =
        new ScaleTransform(1.0, 1.0);


    // ================================================================
    // PRICE CACHE
    // ================================================================

    private readonly Dictionary<string, PriceInfo> _priceCache =
        new(StringComparer.OrdinalIgnoreCase);


    // ================================================================
    // PANNING
    // ================================================================

    private bool _isPanning;

    private Point _panStart;

    private Vector _panOrigin;


    // ================================================================
    // PRICE INFORMATION
    // ================================================================

    private sealed class PriceInfo
    {
        public double Current { get; init; }

        public double PreviousClose { get; init; }

        public bool IsUp =>
            Current > PreviousClose;

        public bool IsDown =>
            Current < PreviousClose;

        public bool IsFlat =>
            Math.Abs(Current - PreviousClose) < 0.000001;

        public double Change =>
            Current - PreviousClose;

        public double ChangePercent =>
            PreviousClose == 0
                ? 0
                : (Change / PreviousClose) * 100.0;

        public string DisplayPrice =>
            $"${Current:F2}";

        public string DisplayChange
        {
            get
            {
                string sign = Change > 0
                    ? "+"
                    : "";

                return $"{sign}{Change:F2} ({sign}{ChangePercent:F2}%)";
            }
        }

        public string ColorHex
        {
            get
            {
                if (IsUp)
                    return "#22C55E";

                if (IsDown)
                    return "#EF4444";

                return "#64748B";
            }
        }
    }


    // ================================================================
    // CONSTRUCTOR
    // ================================================================

    public SupplyChain(
        string symbol,
        string companyName)
    {
        InitializeComponent();

        string targetSymbol =
            string.IsNullOrWhiteSpace(symbol)
                ? "NVDA"
                : symbol.Trim();

        string targetName =
            string.IsNullOrWhiteSpace(companyName)
                ? targetSymbol
                : companyName.Trim();


        CompanyNameText.Text =
            $"{targetName.ToUpper()} ({targetSymbol.ToUpper()}) SUPPLY CHAIN";


        // Apply graph scale.
        GraphCanvas.RenderTransform =
            _scaleTransform;


        // Enable graph panning.
        GraphCanvas.PointerPressed +=
            GraphCanvas_PointerPressed;

        GraphCanvas.PointerMoved +=
            GraphCanvas_PointerMoved;

        GraphCanvas.PointerReleased +=
            GraphCanvas_PointerReleased;


        Dispatcher.UIThread.InvokeAsync(
            async () =>
                await LoadAndBuildGraphAsync(
                    targetSymbol,
                    targetName));
    }


    // ================================================================
    // LOAD JSON + BUILD GRAPH
    // ================================================================

    private async Task LoadAndBuildGraphAsync(
        string symbol,
        string companyName)
    {
        string jsonFileName =
            "sectivia-supply-chain.json";


        string baseDir =
            AppDomain.CurrentDomain.BaseDirectory
            ?? string.Empty;


        string filePath =
            IOPath.Combine(
                baseDir,
                jsonFileName);


        // ------------------------------------------------------------
        // FALLBACK 1
        // ------------------------------------------------------------

        if (!File.Exists(filePath))
        {
            filePath =
                IOPath.Combine(
                    Directory.GetCurrentDirectory(),
                    jsonFileName);
        }


        // ------------------------------------------------------------
        // FALLBACK 2
        // ------------------------------------------------------------

        if (!File.Exists(filePath))
        {
            string candidate =
                IOPath.Combine(
                    baseDir,
                    "..",
                    "..",
                    "..",
                    jsonFileName);


            if (File.Exists(candidate))
            {
                filePath =
                    IOPath.GetFullPath(candidate);
            }
        }


        // ------------------------------------------------------------
        // FILE NOT FOUND
        // ------------------------------------------------------------

        if (!File.Exists(filePath))
        {
            StatusText.Text =
                $"Error: File '{jsonFileName}' not found.";

            StatusText.Foreground =
                Brush.Parse("#F87171");

            return;
        }


        try
        {
            // --------------------------------------------------------
            // READ JSON
            // --------------------------------------------------------

            string jsonContent =
                await File.ReadAllTextAsync(filePath);


            var options =
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    AllowTrailingCommas = true
                };


            var rootData =
                JsonSerializer.Deserialize<SectiviaData>(
                    jsonContent,
                    options);


            if (rootData?.Relations == null)
            {
                StatusText.Text =
                    "Error: JSON file parsed but contains no relation data.";

                StatusText.Foreground =
                    Brush.Parse("#F87171");

                return;
            }


            string searchKey =
                symbol.Trim().ToUpperInvariant();


            // --------------------------------------------------------
            // SUPPLIERS
            // --------------------------------------------------------

            var suppliers =
                rootData.Relations
                    .Where(r =>
                        MatchesTickerOrName(
                            r.GetCustomer(),
                            searchKey))
                    .ToList();


            // --------------------------------------------------------
            // CUSTOMERS
            // --------------------------------------------------------

            var customers =
                rootData.Relations
                    .Where(r =>
                        MatchesTickerOrName(
                            r.GetSupplier(),
                            searchKey))
                    .ToList();


            if (suppliers.Count == 0 &&
                customers.Count == 0)
            {
                StatusText.Text =
                    $"No supply chain matches found in JSON for '{searchKey}'.";

                StatusText.Foreground =
                    Brush.Parse("#F59E0B");

                return;
            }


            // --------------------------------------------------------
            // COLLECT ALL TICKERS
            // --------------------------------------------------------

            var extractedTickers =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    searchKey
                };


            foreach (var relation in suppliers)
            {
                string supplier =
                    relation.GetSupplier();

                if (!string.IsNullOrWhiteSpace(supplier))
                {
                    extractedTickers.Add(
                        supplier.Trim().ToUpperInvariant());
                }
            }


            foreach (var relation in customers)
            {
                string customer =
                    relation.GetCustomer();

                if (!string.IsNullOrWhiteSpace(customer))
                {
                    extractedTickers.Add(
                        customer.Trim().ToUpperInvariant());
                }
            }


            // --------------------------------------------------------
            // FETCH YAHOO DATA
            // --------------------------------------------------------

            StatusText.Text =
                "Loading market data…";

            StatusText.Foreground =
                Brush.Parse("#64748B");


            await FetchYahooPricesAsync(
                extractedTickers);


            // --------------------------------------------------------
            // RENDER
            // --------------------------------------------------------

            RenderGraph(
                searchKey,
                companyName,
                suppliers,
                customers,
                rootData.Companies
                ?? new List<CompanyInfo>());


            StatusText.Text =
                "Drag to pan • Mouse Wheel to zoom • Ctrl + Wheel for fine zoom • Click an edge for details";

            StatusText.Foreground =
                Brush.Parse("#64748B");
        }
        catch (Exception ex)
        {
            StatusText.Text =
                $"JSON Parsing Error: {ex.Message}";

            StatusText.Foreground =
                Brush.Parse("#F87171");
        }
    }


    // ================================================================
    // MATCH TICKER
    // ================================================================

    private bool MatchesTickerOrName(
        string value,
        string target)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return string.Equals(
            value.Trim(),
            target,
            StringComparison.OrdinalIgnoreCase);
    }


    // ================================================================
    // YAHOO FINANCE
    // ================================================================

    private async Task FetchYahooPricesAsync(
        IEnumerable<string> symbols)
    {
        try
        {
            var cleanSymbols =
                symbols
                    .Where(s =>
                        !string.IsNullOrWhiteSpace(s))
                    .Select(s =>
                        s.Trim().ToUpperInvariant())
                    .Where(s =>
                        s.All(char.IsLetterOrDigit))
                    .Distinct(
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray();


            if (cleanSymbols.Length == 0)
                return;


            // --------------------------------------------------------
            // Request both current price and previous close.
            // --------------------------------------------------------

            var securities =
                await Yahoo.Symbols(cleanSymbols)
                    .Fields(
                        Field.Symbol,
                        Field.RegularMarketPrice,
                        Field.RegularMarketPreviousClose)
                    .QueryAsync();


            foreach (var key in securities.Keys)
            {
                var security =
                    securities[key];


                if (security == null)
                    continue;


                try
                {
                    double current =
                        Convert.ToDouble(
                            security[
                                Field.RegularMarketPrice]);


                    double previousClose =
                        Convert.ToDouble(
                            security[
                                Field.RegularMarketPreviousClose]);


                    if (current <= 0)
                        continue;


                    _priceCache[key] =
                        new PriceInfo
                        {
                            Current = current,
                            PreviousClose =
                                previousClose
                        };
                }
                catch
                {
                    // Yahoo may not provide complete
                    // information for every ticker.
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Yahoo API fetch failed: {ex.Message}");
        }
    }


    // ================================================================
    // GRAPH RENDERING
    // ================================================================

    private void RenderGraph(
        string centerSymbol,
        string centerName,
        List<SupplyRelation> suppliers,
        List<SupplyRelation> customers,
        List<CompanyInfo> companies)
    {
        GraphCanvas.Children.Clear();


        // ------------------------------------------------------------
        // CANVAS SIZE
        // ------------------------------------------------------------

        int maxRows =
            Math.Max(
                suppliers.Count,
                customers.Count);


        double requiredHeight =
            Math.Max(
                1000,
                180 + (maxRows * 115));


        GraphCanvas.Width = 1800;

        GraphCanvas.Height =
            requiredHeight;


        double canvasWidth =
            GraphCanvas.Width;


        // ------------------------------------------------------------
        // POSITIONS
        // ------------------------------------------------------------

        double leftX = 70;

        double centerX =
            (canvasWidth / 2) - 140;

        double rightX =
            canvasWidth - 350;


        double centerY =
            (requiredHeight / 2) - 140;


        // ------------------------------------------------------------
        // HEADERS
        // ------------------------------------------------------------

        AddColumnHeader(
            "SUPPLY",
            leftX + 65,
            30,
            "#60A5FA");


        AddColumnHeader(
            $"{centerSymbol} • CORE",
            centerX + 55,
            30,
            "#F8FAFC");


        AddColumnHeader(
            "CUSTOMER",
            rightX + 65,
            30,
            "#FB923C");


        // ------------------------------------------------------------
        // CENTER NODE
        // ------------------------------------------------------------

        PriceInfo? centerPrice =
            _priceCache.TryGetValue(
                centerSymbol,
                out var cp)
                ? cp
                : null;


        Border centerNode =
            CreateCenterNodeCard(
                centerSymbol,
                centerName,
                centerPrice);


        Canvas.SetLeft(
            centerNode,
            centerX);

        Canvas.SetTop(
            centerNode,
            centerY);


        // ------------------------------------------------------------
        // SUPPLIERS
        // ------------------------------------------------------------

        double startYSuppliers = 100;

        double supplierSpacing =
            suppliers.Count > 1
                ? (requiredHeight - 220)
                  / suppliers.Count
                : 120;


        for (int i = 0;
             i < suppliers.Count;
             i++)
        {
            var relation =
                suppliers[i];


            double y =
                startYSuppliers +
                (i *
                 Math.Min(
                     110,
                     supplierSpacing));


            string supplierTicker =
                relation
                    .GetSupplier()
                    .ToUpperInvariant();


            var company =
                companies?
                    .FirstOrDefault(
                        c =>
                            string.Equals(
                                c.Ticker,
                                supplierTicker,
                                StringComparison.OrdinalIgnoreCase));


            string companyName =
                company?.Name
                ?? supplierTicker;


            PriceInfo? price =
                _priceCache.TryGetValue(
                    supplierTicker,
                    out var p)
                    ? p
                    : null;


            string subtext =
                relation.GetSupplierSubtext(
                    company);


            Border supplierNode =
                CreateSideNodeCard(
                    supplierTicker,
                    companyName,
                    subtext,
                    price);


            Canvas.SetLeft(
                supplierNode,
                leftX);

            Canvas.SetTop(
                supplierNode,
                y);


            DrawConnectionLine(
                leftX + 280,
                y + 43,
                centerX,
                centerY + 140,
                relation,
                supplierTicker,
                centerSymbol,
                companyName,
                centerName,
                true,
                companies);


            GraphCanvas.Children.Add(
                supplierNode);
        }


        // ------------------------------------------------------------
        // CUSTOMERS
        // ------------------------------------------------------------

        double startYCustomers = 100;

        double customerSpacing =
            customers.Count > 1
                ? (requiredHeight - 220)
                  / customers.Count
                : 120;


        for (int i = 0;
             i < customers.Count;
             i++)
        {
            var relation =
                customers[i];


            double y =
                startYCustomers +
                (i *
                 Math.Min(
                     110,
                     customerSpacing));


            string customerTicker =
                relation
                    .GetCustomer()
                    .ToUpperInvariant();


            var company =
                companies?
                    .FirstOrDefault(
                        c =>
                            string.Equals(
                                c.Ticker,
                                customerTicker,
                                StringComparison.OrdinalIgnoreCase));


            string companyName =
                company?.Name
                ?? customerTicker;


            PriceInfo? price =
                _priceCache.TryGetValue(
                    customerTicker,
                    out var p)
                    ? p
                    : null;


            string subtext =
                relation.GetCustomerSubtext(
                    company);


            Border customerNode =
                CreateSideNodeCard(
                    customerTicker,
                    companyName,
                    subtext,
                    price);


            Canvas.SetLeft(
                customerNode,
                rightX);

            Canvas.SetTop(
                customerNode,
                y);


            DrawConnectionLine(
                centerX + 280,
                centerY + 140,
                rightX,
                y + 43,
                relation,
                centerSymbol,
                customerTicker,
                centerName,
                companyName,
                false,
                companies);


            GraphCanvas.Children.Add(
                customerNode);
        }


        // ------------------------------------------------------------
        // Put center node on top of connections.
        // ------------------------------------------------------------

        GraphCanvas.Children.Add(
            centerNode);
    }


    // ================================================================
    // COLUMN HEADER
    // ================================================================

    private void AddColumnHeader(
        string text,
        double x,
        double y,
        string colorHex)
    {
        var textBlock =
            new TextBlock
            {
                Text = text,

                FontSize = 13,

                FontWeight =
                    FontWeight.Bold,

                Foreground =
                    Brush.Parse(colorHex),

                LetterSpacing = 1.2
            };


        Canvas.SetLeft(
            textBlock,
            x);

        Canvas.SetTop(
            textBlock,
            y);


        GraphCanvas.Children.Add(
            textBlock);
    }


    // ================================================================
    // CENTER NODE
    // ================================================================

    private Border CreateCenterNodeCard(
        string symbol,
        string name,
        PriceInfo? price)
    {
        bool hasPrice =
            price != null;


        string accent =
            !hasPrice
                ? "#64748B"
                : price!.ColorHex;


        var border =
            new Border
            {
                Width = 280,

                Height = 280,

                Background =
                    Brush.Parse("#151A21"),

                BorderBrush =
                    Brush.Parse(accent),

                BorderThickness =
                    new Thickness(3),

                CornerRadius =
                    new CornerRadius(140),

                Padding =
                    new Thickness(24),

                BoxShadow =
                    new BoxShadows(
                        new BoxShadow
                        {
                            Blur = 28,
                            Spread = 2,
                            OffsetX = 0,
                            OffsetY = 0,
                            Color =
                                Color.Parse(
                                    accent + "55")
                        }),

                Cursor =
                    new Cursor(
                        StandardCursorType.Hand)
            };


        var panel =
            new StackPanel
            {
                VerticalAlignment =
                    Avalonia.Layout.VerticalAlignment.Center,

                HorizontalAlignment =
                    Avalonia.Layout.HorizontalAlignment.Center,

                Spacing = 5
            };


        // ------------------------------------------------------------
        // TICKER
        // ------------------------------------------------------------

        panel.Children.Add(
            new TextBlock
            {
                Text = symbol,

                FontWeight =
                    FontWeight.Bold,

                FontSize = 26,

                Foreground =
                    Brushes.White,

                HorizontalAlignment =
                    Avalonia.Layout.HorizontalAlignment.Center
            });


        // ------------------------------------------------------------
        // COMPANY
        // ------------------------------------------------------------

        panel.Children.Add(
            new TextBlock
            {
                Text = name,

                FontSize = 13,

                Foreground =
                    Brush.Parse("#94A3B8"),

                TextAlignment =
                    TextAlignment.Center,

                TextWrapping =
                    TextWrapping.Wrap,

                MaxWidth = 215,

                HorizontalAlignment =
                    Avalonia.Layout.HorizontalAlignment.Center
            });


        // ------------------------------------------------------------
        // PRICE
        // ------------------------------------------------------------

        if (hasPrice)
        {
            panel.Children.Add(
                new TextBlock
                {
                    Text =
                        price!.DisplayPrice,

                    FontSize = 21,

                    FontWeight =
                        FontWeight.Bold,

                    Foreground =
                        Brush.Parse(accent),

                    HorizontalAlignment =
                        Avalonia.Layout.HorizontalAlignment.Center,

                    Margin =
                        new Thickness(
                            0,
                            10,
                            0,
                            0)
                });


            panel.Children.Add(
                new TextBlock
                {
                    Text =
                        price.DisplayChange,

                    FontSize = 12,

                    Foreground =
                        Brush.Parse(accent),

                    HorizontalAlignment =
                        Avalonia.Layout.HorizontalAlignment.Center
                });


            panel.Children.Add(
                new TextBlock
                {
                    Text =
                        price.IsUp
                            ? "▲ UP"
                            : price.IsDown
                                ? "▼ DOWN"
                                : "● FLAT",

                    FontSize = 10,

                    FontWeight =
                        FontWeight.Bold,

                    Foreground =
                        Brush.Parse(accent),

                    HorizontalAlignment =
                        Avalonia.Layout.HorizontalAlignment.Center,

                    Margin =
                        new Thickness(
                            0,
                            4,
                            0,
                            0)
                });
        }
        else
        {
            panel.Children.Add(
                new TextBlock
                {
                    Text =
                        "MARKET DATA UNAVAILABLE",

                    FontSize = 9,

                    Foreground =
                        Brush.Parse("#64748B"),

                    HorizontalAlignment =
                        Avalonia.Layout.HorizontalAlignment.Center,

                    Margin =
                        new Thickness(
                            0,
                            10,
                            0,
                            0)
                });
        }


        border.Child =
            panel;


        return border;
    }


    // ================================================================
    // SIDE NODE
    // ================================================================

    private Border CreateSideNodeCard(
        string symbol,
        string name,
        string subtext,
        PriceInfo? price)
    {
        bool hasPrice =
            price != null;


        string accent =
            !hasPrice
                ? "#475569"
                : price!.ColorHex;


        var border =
            new Border
            {
                Width = 280,

                Height = 86,

                Background =
                    Brush.Parse("#151A21"),

                BorderBrush =
                    Brush.Parse(accent + "AA"),

                BorderThickness =
                    new Thickness(1.5),

                CornerRadius =
                    new CornerRadius(12),

                Padding =
                    new Thickness(
                        14,
                        10),

                BoxShadow =
                    new BoxShadows(
                        new BoxShadow
                        {
                            Blur = 14,
                            Spread = 0,
                            OffsetX = 0,
                            OffsetY = 3,
                            Color =
                                Color.Parse(
                                    "#00000066")
                        }),

                Cursor =
                    new Cursor(
                        StandardCursorType.Hand)
            };


        var grid =
            new Grid
            {
                ColumnDefinitions =
                    new ColumnDefinitions(
                        "*,Auto")
            };


        // ------------------------------------------------------------
        // LEFT INFORMATION
        // ------------------------------------------------------------

        var leftPanel =
            new StackPanel
            {
                VerticalAlignment =
                    Avalonia.Layout.VerticalAlignment.Center
            };


        leftPanel.Children.Add(
            new TextBlock
            {
                Text = symbol,

                FontWeight =
                    FontWeight.Bold,

                FontSize = 14,

                Foreground =
                    Brushes.White,

                TextTrimming =
                    TextTrimming.CharacterEllipsis
            });


        leftPanel.Children.Add(
            new TextBlock
            {
                Text = name,

                FontSize = 11,

                Foreground =
                    Brush.Parse("#94A3B8"),

                TextTrimming =
                    TextTrimming.CharacterEllipsis,

                Margin =
                    new Thickness(
                        0,
                        2,
                        0,
                        0)
            });


        leftPanel.Children.Add(
            new TextBlock
            {
                Text = subtext,

                FontSize = 10,

                Foreground =
                    Brush.Parse("#64748B"),

                TextTrimming =
                    TextTrimming.CharacterEllipsis,

                Margin =
                    new Thickness(
                        0,
                        3,
                        0,
                        0)
            });


        Grid.SetColumn(
            leftPanel,
            0);


        grid.Children.Add(
            leftPanel);


        // ------------------------------------------------------------
        // PRICE
        // ------------------------------------------------------------

        if (hasPrice)
        {
            var pricePanel =
                new StackPanel
                {
                    HorizontalAlignment =
                        Avalonia.Layout.HorizontalAlignment.Right,

                    VerticalAlignment =
                        Avalonia.Layout.VerticalAlignment.Center
                };


            pricePanel.Children.Add(
                new TextBlock
                {
                    Text =
                        price!.DisplayPrice,

                    FontSize = 13,

                    FontWeight =
                        FontWeight.Bold,

                    Foreground =
                        Brush.Parse(accent),

                    HorizontalAlignment =
                        Avalonia.Layout.HorizontalAlignment.Right
                });


            pricePanel.Children.Add(
                new TextBlock
                {
                    Text =
                        price.DisplayChange,

                    FontSize = 9,

                    Foreground =
                        Brush.Parse(accent),

                    HorizontalAlignment =
                        Avalonia.Layout.HorizontalAlignment.Right,

                    Margin =
                        new Thickness(
                            0,
                            2,
                            0,
                            0)
                });


            Grid.SetColumn(
                pricePanel,
                1);


            grid.Children.Add(
                pricePanel);
        }


        border.Child =
            grid;


        // ------------------------------------------------------------
        // Hover animation-ish effect.
        // ------------------------------------------------------------

        border.PointerEntered +=
            (s, e) =>
            {
                border.BorderBrush =
                    Brush.Parse(accent);

                border.BorderThickness =
                    new Thickness(2);
            };


        border.PointerExited +=
            (s, e) =>
            {
                border.BorderBrush =
                    Brush.Parse(accent + "AA");

                border.BorderThickness =
                    new Thickness(1.5);
            };


        return border;
    }


    // ================================================================
    // CONNECTION EDGE
    // ================================================================

    private void DrawConnectionLine(
        double x1,
        double y1,
        double x2,
        double y2,
        SupplyRelation relation,
        string sourceSymbol,
        string targetSymbol,
        string sourceName,
        string targetName,
        bool isInput,
        List<CompanyInfo> companies)
    {
        // ------------------------------------------------------------
        // Determine market colour.
        //
        // We use the source company's market direction.
        // If source data isn't available, use the target.
        // ------------------------------------------------------------

        PriceInfo? sourcePrice =
            _priceCache.TryGetValue(
                sourceSymbol,
                out var sp)
                ? sp
                : null;


        PriceInfo? targetPrice =
            _priceCache.TryGetValue(
                targetSymbol,
                out var tp)
                ? tp
                : null;


        PriceInfo? marketPrice =
            sourcePrice ?? targetPrice;


        string colorHex =
            marketPrice?.ColorHex
            ?? "#475569";


        // ------------------------------------------------------------
        // Main connection
        // ------------------------------------------------------------

        var line =
            new Line
            {
                StartPoint =
                    new Point(
                        x1,
                        y1),

                EndPoint =
                    new Point(
                        x2,
                        y2),

                Stroke =
                    Brush.Parse(colorHex),

                StrokeThickness = 2.5,

                Cursor =
                    new Cursor(
                        StandardCursorType.Hand)
            };


        // ------------------------------------------------------------
        // Hover
        // ------------------------------------------------------------

        line.PointerEntered +=
            (s, e) =>
            {
                line.StrokeThickness = 5;
            };


        line.PointerExited +=
            (s, e) =>
            {
                line.StrokeThickness = 2.5;
            };


        // ------------------------------------------------------------
        // Click
        // ------------------------------------------------------------

        line.PointerPressed +=
            (s, e) =>
            {
                ShowRelationshipModal(
                    relation,
                    sourceSymbol,
                    targetSymbol,
                    sourceName,
                    targetName,
                    companies);

                e.Handled = true;
            };


        GraphCanvas.Children.Add(
            line);


        // ------------------------------------------------------------
        // Relationship badge
        // ------------------------------------------------------------

        string label =
            relation.GetEdgeBadgeLabel(
                isInput);


        if (!string.IsNullOrWhiteSpace(label))
        {
            var badge =
                new Border
                {
                    Background =
                        Brush.Parse("#111827"),

                    BorderBrush =
                        Brush.Parse(colorHex),

                    BorderThickness =
                        new Thickness(1),

                    CornerRadius =
                        new CornerRadius(6),

                    Padding =
                        new Thickness(
                            8,
                            4),

                    Cursor =
                        new Cursor(
                            StandardCursorType.Hand)
                };


            badge.Child =
                new TextBlock
                {
                    Text = label,

                    FontSize = 9,

                    Foreground =
                        Brushes.White,

                    FontWeight =
                        FontWeight.SemiBold
                };


            badge.PointerEntered +=
                (s, e) =>
                {
                    badge.BorderBrush =
                        Brushes.White;
                };


            badge.PointerExited +=
                (s, e) =>
                {
                    badge.BorderBrush =
                        Brush.Parse(colorHex);
                };


            badge.PointerPressed +=
                (s, e) =>
                {
                    ShowRelationshipModal(
                        relation,
                        sourceSymbol,
                        targetSymbol,
                        sourceName,
                        targetName,
                        companies);

                    e.Handled = true;
                };


            double midX =
                (x1 + x2) / 2 - 40;


            double midY =
                (y1 + y2) / 2 - 12;


            Canvas.SetLeft(
                badge,
                midX);


            Canvas.SetTop(
                badge,
                midY);


            GraphCanvas.Children.Add(
                badge);
        }
    }


    // ================================================================
    // RELATIONSHIP MODAL
    // ================================================================

    private void ShowRelationshipModal(
        SupplyRelation relation,
        string sourceSymbol,
        string targetSymbol,
        string sourceName,
        string targetName,
        List<CompanyInfo> companies)
    {
        ModalHeaderTitle.Text =
            $"{sourceName}  ➔  {targetName}";


        ModalDirection.Text =
            $"{sourceSymbol}  ➔  {targetSymbol}";


        var sourceCompany =
            companies?
                .FirstOrDefault(
                    c =>
                        string.Equals(
                            c.Ticker,
                            sourceSymbol,
                            StringComparison.OrdinalIgnoreCase));


        ModalCategory.Text =
            relation.GetCategoryDisplay(
                sourceCompany);


        ModalFunction.Text =
            relation.GetFunctionDisplay(
                sourceSymbol,
                targetSymbol,
                sourceName,
                targetName,
                sourceCompany);


        RelationshipOverlay.IsVisible =
            true;
    }


    // ================================================================
    // CLOSE MODAL
    // ================================================================

    private void CloseModal_Click(
        object? sender,
        RoutedEventArgs e)
    {
        RelationshipOverlay.IsVisible =
            false;
    }


    // ================================================================
    // MOUSE WHEEL ZOOM
    // ================================================================

    private void OnPointerWheelChanged(
        object? sender,
        PointerWheelEventArgs e)
    {
        bool ctrlPressed =
            e.KeyModifiers.HasFlag(
                KeyModifiers.Control);


        // Normal wheel = 10%.
        // Ctrl + wheel = 5%.
        double step =
            ctrlPressed
                ? 0.05
                : 0.10;


        if (e.Delta.Y > 0)
        {
            _zoomFactor =
                Math.Min(
                    _zoomFactor + step,
                    2.5);
        }
        else
        {
            _zoomFactor =
                Math.Max(
                    _zoomFactor - step,
                    0.35);
        }


        _scaleTransform.ScaleX =
            _zoomFactor;

        _scaleTransform.ScaleY =
            _zoomFactor;


        e.Handled = true;
    }


    // ================================================================
    // GRAPH PAN - PRESS
    // ================================================================

    private void GraphCanvas_PointerPressed(
        object? sender,
        PointerPressedEventArgs e)
    {
        var point =
            e.GetCurrentPoint(
                GraphCanvas);


        bool middle =
            point.Properties.IsMiddleButtonPressed;


        bool left =
            point.Properties.IsLeftButtonPressed;


        // Left click + Ctrl can also pan.
        bool ctrlPan =
            e.KeyModifiers.HasFlag(
                KeyModifiers.Control)
            && left;


        if (middle || ctrlPan)
        {
            _isPanning = true;


            _panStart =
                point.Position;


            _panOrigin =
                new Vector(
                    GraphScrollViewer.Offset.X,
                    GraphScrollViewer.Offset.Y);


            GraphCanvas.Cursor =
                new Cursor(
                    StandardCursorType.Hand);


            e.Pointer.Capture(
                GraphCanvas);


            e.Handled = true;
        }
    }


    // ================================================================
    // GRAPH PAN - MOVE
    // ================================================================

    private void GraphCanvas_PointerMoved(
        object? sender,
        PointerEventArgs e)
    {
        if (!_isPanning)
            return;


        var current =
            e.GetPosition(
                GraphCanvas);


        double dx =
            _panStart.X -
            current.X;


        double dy =
            _panStart.Y -
            current.Y;


        var newOffset =
            new Vector(
                Math.Max(
                    0,
                    _panOrigin.X + dx),

                Math.Max(
                    0,
                    _panOrigin.Y + dy));


        GraphScrollViewer.Offset =
            newOffset;


        e.Handled = true;
    }


    // ================================================================
    // GRAPH PAN - RELEASE
    // ================================================================

    private void GraphCanvas_PointerReleased(
        object? sender,
        PointerReleasedEventArgs e)
    {
        if (!_isPanning)
            return;


        _isPanning = false;


        e.Pointer.Capture(null);


        GraphCanvas.Cursor =
            new Cursor(
                StandardCursorType.Arrow);


        e.Handled = true;
    }
}


// ====================================================================
// JSON SCHEMA
// ====================================================================

#region Expanded Schema Matching All Sectivia JSON Keys

public class SectiviaData
{
    [JsonPropertyName("companies")]
    public List<CompanyInfo>? Companies { get; set; }


    [JsonPropertyName("relations")]
    public List<SupplyRelation>? Relations { get; set; }
}


public class CompanyInfo
{
    [JsonPropertyName("ticker")]
    public string? Ticker { get; set; }


    [JsonPropertyName("name")]
    public string? Name { get; set; }


    [JsonPropertyName("segment")]
    public string? Segment { get; set; }


    [JsonPropertyName("sector")]
    public string? Sector { get; set; }
}


public class SupplyRelation
{
    [JsonPropertyName("supplierTicker")]
    public string? SupplierTicker { get; set; }


    [JsonPropertyName("customerTicker")]
    public string? CustomerTicker { get; set; }


    [JsonPropertyName("supplier")]
    public string? Supplier { get; set; }


    [JsonPropertyName("customer")]
    public string? Customer { get; set; }


    [JsonPropertyName("supplierSegment")]
    public string? SupplierSegment { get; set; }


    [JsonPropertyName("customerSegment")]
    public string? CustomerSegment { get; set; }


    [JsonPropertyName("segment")]
    public string? Segment { get; set; }


    [JsonPropertyName("sector")]
    public string? Sector { get; set; }


    [JsonPropertyName("category")]
    public string? Category { get; set; }


    [JsonPropertyName("relationship")]
    public string? Relationship { get; set; }


    [JsonPropertyName("description")]
    public string? Description { get; set; }


    [JsonPropertyName("details")]
    public string? Details { get; set; }


    [JsonPropertyName("function")]
    public string? Function { get; set; }


    [JsonPropertyName("explanation")]
    public string? Explanation { get; set; }


    [JsonPropertyName("notes")]
    public string? Notes { get; set; }


    [JsonPropertyName("summary")]
    public string? Summary { get; set; }


    // ================================================================
    // TICKERS
    // ================================================================

    public string GetSupplier()
    {
        return !string.IsNullOrWhiteSpace(
            SupplierTicker)
            ? SupplierTicker
            : Supplier ?? string.Empty;
    }


    public string GetCustomer()
    {
        return !string.IsNullOrWhiteSpace(
            CustomerTicker)
            ? CustomerTicker
            : Customer ?? string.Empty;
    }


    // ================================================================
    // SUPPLIER SUBTEXT
    // ================================================================

    public string GetSupplierSubtext(
        CompanyInfo? company)
    {
        if (!string.IsNullOrWhiteSpace(
            SupplierSegment))
        {
            return SupplierSegment;
        }


        if (company != null &&
            !string.IsNullOrWhiteSpace(
                company.Segment))
        {
            return company.Segment;
        }


        if (!string.IsNullOrWhiteSpace(
            Segment) &&
            !string.Equals(
                Segment,
                "ai",
                StringComparison.OrdinalIgnoreCase))
        {
            return Segment;
        }


        return "Supplier";
    }


    // ================================================================
    // CUSTOMER SUBTEXT
    // ================================================================

    public string GetCustomerSubtext(
        CompanyInfo? company)
    {
        if (!string.IsNullOrWhiteSpace(
            CustomerSegment))
        {
            return CustomerSegment;
        }


        if (company != null &&
            !string.IsNullOrWhiteSpace(
                company.Segment))
        {
            return company.Segment;
        }


        if (!string.IsNullOrWhiteSpace(
            Segment) &&
            !string.Equals(
                Segment,
                "ai",
                StringComparison.OrdinalIgnoreCase))
        {
            return Segment;
        }


        return "Customer";
    }


    // ================================================================
    // CATEGORY
    // ================================================================

    public string GetCategoryDisplay(
        CompanyInfo? company = null)
    {
        string rawCategory =
            string.Empty;


        if (!string.IsNullOrWhiteSpace(
            Category))
        {
            rawCategory =
                Category;
        }
        else if (!string.IsNullOrWhiteSpace(
            SupplierSegment))
        {
            rawCategory =
                SupplierSegment;
        }
        else if (!string.IsNullOrWhiteSpace(
            CustomerSegment))
        {
            rawCategory =
                CustomerSegment;
        }
        else if (company != null &&
                 !string.IsNullOrWhiteSpace(
                     company.Segment))
        {
            rawCategory =
                company.Segment;
        }
        else if (!string.IsNullOrWhiteSpace(
            Segment) &&
            !string.Equals(
                Segment,
                "ai",
                StringComparison.OrdinalIgnoreCase))
        {
            rawCategory =
                Segment;
        }
        else if (!string.IsNullOrWhiteSpace(
            Sector))
        {
            rawCategory =
                Sector;
        }


        rawCategory =
            rawCategory
                .Trim()
                .ToLowerInvariant();


        return rawCategory switch
        {
            "equip" =>
                "Semiconductor equipment & IP (equip)",

            "chips" =>
                "Semiconductors & Processors (chips)",

            "datacentre" or
            "datacenter" or
            "data center" =>
                "Data centers (datacentre)",

            "cloud" =>
                "Cloud Infrastructure & Hosting (cloud)",

            "consult" =>
                "Enterprise Consulting & Integration (consult)",

            "aisw" =>
                "AI Software & Enterprise Platforms (aisw)",

            _ =>
                !string.IsNullOrWhiteSpace(
                    rawCategory)
                    ? rawCategory
                    : "General Supply Chain Relationship"
        };
    }


    // ================================================================
    // FUNCTION
    // ================================================================

    public string GetFunctionDisplay(
        string sourceSymbol,
        string targetSymbol,
        string sourceName,
        string targetName,
        CompanyInfo? company = null)
    {
        // ------------------------------------------------------------
        // First use custom JSON description.
        // ------------------------------------------------------------

        var textCandidates =
            new[]
            {
                Function,
                Explanation,
                Description,
                Details,
                Notes,
                Summary
            };


        foreach (var text in textCandidates)
        {
            if (!string.IsNullOrWhiteSpace(text) &&
                text.Length > 20)
            {
                return text;
            }
        }


        string seg =
            (GetCategoryDisplay(company)
             ?? string.Empty)
            .ToLowerInvariant();


        string sourceTicker =
            sourceSymbol
                .Trim()
                .ToUpperInvariant();


        // ------------------------------------------------------------
        // TICKER-SPECIFIC RULES
        // ------------------------------------------------------------

        if (sourceTicker == "SNPS" ||
            sourceTicker == "CDNS")
        {
            return
                $"Supplies Electronic Design Automation (EDA) software, IP cores, and silicon design/verification tools used to design {targetName} GPU and compute architectures.";
        }


        if (sourceTicker == "ARM")
        {
            return
                $"Licenses processor architecture designs and instruction sets used in {targetName}'s CPU platforms (such as Grace Hopper and Grace Blackwell superchips).";
        }


        if (sourceTicker == "MU")
        {
            return
                $"Supplies high-bandwidth memory (HBM3e/HBM4) and high-performance DRAM required for {targetName}'s enterprise AI compute modules.";
        }


        if (sourceTicker == "MPWR")
        {
            return
                $"Provides high-efficiency power management ICs (PMICs) and power delivery solutions for {targetName}'s high-density server racks.";
        }


        if (sourceTicker == "APH")
        {
            return
                $"{sourceName} manufactures high-speed connectors, physical cabling, and interconnect systems. These critical hardware components form the backplane architecture (e.g., NVLink interconnects) used in {targetName}'s AI servers and GPU clusters (such as the Blackwell GB200 systems).";
        }


        // ------------------------------------------------------------
        // CATEGORY FALLBACKS
        // ------------------------------------------------------------

        if (seg.Contains("equip"))
        {
            return
                $"Provides specialized semiconductor design tools, equipment, or intellectual property required for developing {targetName}'s hardware platforms.";
        }


        if (seg.Contains("chips"))
        {
            return
                $"Supplies memory, processor IP, or semiconductor components integrated directly into {targetName}'s computing hardware.";
        }


        if (seg.Contains("datacentre") ||
            seg.Contains("datacenter"))
        {
            return
                $"Supplies critical infrastructure, interconnects, or physical hosting facilities powering {targetName}'s data center ecosystem.";
        }


        if (seg.Contains("cloud"))
        {
            return
                $"Deploys and hosts cloud infrastructure instances powered by {targetName}'s high-performance AI accelerator chips.";
        }


        if (seg.Contains("consult"))
        {
            return
                $"Provides enterprise integration, system design, and advisory services to deploy {targetName}'s AI and compute solutions for corporate clients.";
        }


        if (seg.Contains("aisw"))
        {
            return
                $"Integrates {targetName}'s GPU hardware and software stacks to train, fine-tune, and deploy large-scale enterprise AI models.";
        }


        return
            $"Provides goods or services supporting the operations and product lifecycle of {targetName}.";
    }


    // ================================================================
    // EDGE BADGE
    // ================================================================

    public string GetEdgeBadgeLabel(
        bool isSupplier)
    {
        if (!string.IsNullOrWhiteSpace(
            Relationship))
        {
            return Relationship;
        }


        return isSupplier
            ? $"Supplies {GetSupplier()}"
            : $"Sells to {GetCustomer()}";
    }
}

#endregion