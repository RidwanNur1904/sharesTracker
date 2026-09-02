using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using DotNetEnv;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace sharesTracker;

public partial class MainWindow : Window
{
    private readonly HttpClient _httpClient = new();

    private readonly string _apiKey;

    private List<double> _prices = new();


    public MainWindow()
    {
        InitializeComponent();

        // Load .env — TraversePath() walks up from the current working
        // directory (e.g. bin/Debug/net8.0) until it finds a .env file,
        // instead of only checking the exe's output folder.
        try
        {
            Env.TraversePath().Load();
        }
        catch (Exception ex)
        {
            StatusText.Text =
                $"ERROR: could not load .env ({ex.Message})";

            return;
        }

        // Get Alpha Vantage API key
        _apiKey =
            Environment.GetEnvironmentVariable(
                "ALPHA_VANTAGE_API_KEY")
            ?? "";

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            StatusText.Text =
                "ERROR: ALPHA_VANTAGE_API_KEY not found in .env";

            return;
        }

        StocksList.SelectedIndex = 0;

        Loaded += async (_, _) =>
        {
            await LoadStockAsync("AAPL");
        };

        GraphPanel.SizeChanged += (_, _) =>
        {
            GraphPanel.InvalidateVisual();
        };
    }


    // =========================================================
    // WATCHLIST
    // =========================================================

    private async void Stock_Selected(
        object? sender,
        SelectionChangedEventArgs e)
    {
        if (StocksList.SelectedItem is ListBoxItem item)
        {
            string symbol =
                item.Content?.ToString() ?? "";

            if (!string.IsNullOrWhiteSpace(symbol))
            {
                await LoadStockAsync(symbol);
            }
        }
    }


    // =========================================================
    // SEARCH
    // =========================================================

    private async void Search_Click(
        object? sender,
        RoutedEventArgs e)
    {
        string symbol =
            SearchBox.Text?.Trim().ToUpper() ?? "";

        if (string.IsNullOrWhiteSpace(symbol))
        {
            StatusText.Text =
                "Enter a stock symbol.";

            return;
        }

        await LoadStockAsync(symbol);
    }


    // =========================================================
    // LOAD STOCK
    // =========================================================

    private async Task LoadStockAsync(
        string symbol)
    {
        try
        {
            StatusText.Text =
                $"Loading {symbol}...";


            // =================================================
            // CURRENT QUOTE
            // =================================================

            string quoteUrl =
                "https://www.alphavantage.co/query" +
                "?function=GLOBAL_QUOTE" +
                $"&symbol={Uri.EscapeDataString(symbol)}" +
                $"&apikey={_apiKey}";


            string quoteJson =
                await _httpClient.GetStringAsync(
                    quoteUrl);


            using JsonDocument quoteDocument =
                JsonDocument.Parse(quoteJson);


            if (!quoteDocument.RootElement.TryGetProperty(
                    "Global Quote",
                    out JsonElement quote))
            {
                StatusText.Text =
                    "No quote data returned.";

                return;
            }


            // =================================================
            // READ QUOTE
            // =================================================

            string price =
                GetValue(
                    quote,
                    "05. price");

            string open =
                GetValue(
                    quote,
                    "02. open");

            string high =
                GetValue(
                    quote,
                    "03. high");

            string low =
                GetValue(
                    quote,
                    "04. low");

            string previousClose =
                GetValue(
                    quote,
                    "08. previous close");

            string volume =
                GetValue(
                    quote,
                    "06. volume");

            string change =
                GetValue(
                    quote,
                    "09. change");

            string changePercent =
                GetValue(
                    quote,
                    "10. change percent");


            // =================================================
            // UPDATE UI
            // =================================================

            StockSymbol.Text =
                symbol;

            StockName.Text =
                symbol;

            StockPrice.Text =
                FormatPrice(price);

            OpenText.Text =
                FormatPrice(open);

            HighText.Text =
                FormatPrice(high);

            LowText.Text =
                FormatPrice(low);

            PreviousCloseText.Text =
                FormatPrice(previousClose);

            VolumeText.Text =
                volume;

            ChangeText.Text =
                change;

            ChangePercentText.Text =
                changePercent;


            // =================================================
            // PRICE COLOUR
            // =================================================

            bool negative =
                change.Trim().StartsWith("-");


            IBrush priceColour;

            if (negative)
            {
                priceColour =
                    new SolidColorBrush(
                        Color.Parse("#F44747"));
            }
            else
            {
                priceColour =
                    new SolidColorBrush(
                        Color.Parse("#4EC9B0"));
            }


            StockPrice.Foreground =
                priceColour;

            ChangeText.Foreground =
                priceColour;

            ChangePercentText.Foreground =
                priceColour;


            // =================================================
            // LOAD GRAPH
            // =================================================

            await LoadGraphAsync(symbol);


            StatusText.Text =
                $"{symbol} loaded successfully";
        }
        catch (Exception ex)
        {
            StatusText.Text =
                $"Error: {ex.Message}";
        }
    }


    // =========================================================
    // LOAD HISTORICAL DATA
    // =========================================================

    private async Task LoadGraphAsync(
        string symbol)
    {
        string url =
            "https://www.alphavantage.co/query" +
            "?function=TIME_SERIES_DAILY" +
            $"&symbol={Uri.EscapeDataString(symbol)}" +
            "&outputsize=compact" +
            $"&apikey={_apiKey}";


        string json =
            await _httpClient.GetStringAsync(url);


        using JsonDocument document =
            JsonDocument.Parse(json);


        // API error
        if (document.RootElement.TryGetProperty(
                "Error Message",
                out _))
        {
            StatusText.Text =
                "Historical data unavailable.";

            return;
        }


        // API rate limit
        if (document.RootElement.TryGetProperty(
                "Note",
                out JsonElement note))
        {
            StatusText.Text =
                note.GetString() ??
                "Alpha Vantage request limit reached.";

            return;
        }


        if (!document.RootElement.TryGetProperty(
                "Time Series (Daily)",
                out JsonElement timeSeries))
        {
            StatusText.Text =
                "No historical data returned.";

            return;
        }


        _prices.Clear();


        // =================================================
        // GET CLOSING PRICES
        // =================================================

        foreach (JsonProperty day
                 in timeSeries.EnumerateObject())
        {
            if (day.Value.TryGetProperty(
                    "4. close",
                    out JsonElement close))
            {
                string? value =
                    close.GetString();


                if (double.TryParse(
                        value,
                        NumberStyles.Any,
                        CultureInfo.InvariantCulture,
                        out double parsedPrice))
                {
                    _prices.Add(parsedPrice);
                }
            }
        }


        // Newest → oldest from API.
        // Reverse to oldest → newest.
        _prices =
            _prices
                .Take(60)
                .Reverse()
                .ToList();


        if (_prices.Count < 2)
        {
            StatusText.Text =
                "Not enough data to draw graph.";

            return;
        }


        // Tell Avalonia to redraw
        GraphPanel.InvalidateVisual();
    }


    // =========================================================
    // GRAPH
    // =========================================================

    private void DrawGraph(
        DrawingContext context)
    {
        if (_prices.Count < 2)
            return;


        double width =
            GraphPanel.Bounds.Width;


        double height =
            GraphPanel.Bounds.Height;


        if (width <= 10 ||
            height <= 10)
        {
            return;
        }


        double padding = 25;


        double graphWidth =
            width - padding * 2;


        double graphHeight =
            height - padding * 2;


        double min =
            _prices.Min();


        double max =
            _prices.Max();


        double range =
            max - min;


        if (range <= 0)
            range = 1;


        // =================================================
        // GRID
        // =================================================

        var gridPen =
            new Pen(
                new SolidColorBrush(
                    Color.Parse("#2D2D30")),
                1);


        for (int i = 0; i <= 4; i++)
        {
            double y =
                padding +
                (i * graphHeight / 4);


            context.DrawLine(
                gridPen,
                new Point(
                    padding,
                    y),
                new Point(
                    width - padding,
                    y));
        }


        // =================================================
        // PRICE LINE
        // =================================================

        var graphPen =
            new Pen(
                new SolidColorBrush(
                    Color.Parse("#4EC9B0")),
                2);


        for (int i = 0;
             i < _prices.Count - 1;
             i++)
        {
            double x1 =
                padding +
                i *
                graphWidth /
                (_prices.Count - 1);


            double x2 =
                padding +
                (i + 1) *
                graphWidth /
                (_prices.Count - 1);


            double y1 =
                padding +
                graphHeight -
                (
                    (_prices[i] - min)
                    / range
                    * graphHeight
                );


            double y2 =
                padding +
                graphHeight -
                (
                    (_prices[i + 1] - min)
                    / range
                    * graphHeight
                );


            context.DrawLine(
                graphPen,
                new Point(x1, y1),
                new Point(x2, y2));
        }
    }


    // =========================================================
    // AVALONIA RENDERING
    // =========================================================

    private void GraphPanel_Render(
        object? sender,
        DrawingContext context)
    {
        DrawGraph(context);
    }


    // =========================================================
    // JSON HELPER
    // =========================================================

    private static string GetValue(
        JsonElement element,
        string property)
    {
        if (element.TryGetProperty(
                property,
                out JsonElement value))
        {
            return value.GetString() ?? "--";
        }


        return "--";
    }


    // =========================================================
    // PRICE FORMAT
    // =========================================================

    private static string FormatPrice(
        string value)
    {
        if (double.TryParse(
                value,
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out double number))
        {
            return number.ToString(
                "0.00",
                CultureInfo.InvariantCulture);
        }


        return value;
    }
}