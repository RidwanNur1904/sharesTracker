using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using DotNetEnv;
using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace sharesTracker;

public partial class MainWindow : Window
{
    private readonly HttpClient _httpClient = new();
    private readonly string _apiKey;

    public MainWindow()
    {
        InitializeComponent();

        try
        {
            Env.TraversePath().Load();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"ERROR: could not load .env ({ex.Message})";
            return;
        }

        _apiKey = Environment.GetEnvironmentVariable("ALPHA_VANTAGE_API_KEY") ?? "";

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            StatusText.Text = "ERROR: ALPHA_VANTAGE_API_KEY not found in .env";
            return;
        }

        StocksList.SelectedIndex = 0;

        Loaded += async (_, _) =>
        {
            await LoadStockAsync("AAPL");
        };
    }

    // =========================================================
    // WATCHLIST
    // =========================================================

    private async void Stock_Selected(object? sender, SelectionChangedEventArgs e)
    {
        if (StocksList.SelectedItem is ListBoxItem item)
        {
            string symbol = item.Content?.ToString() ?? "";

            if (!string.IsNullOrWhiteSpace(symbol))
            {
                await LoadStockAsync(symbol);
            }
        }
    }

    // =========================================================
    // SEARCH
    // =========================================================

    private async void Search_Click(object? sender, RoutedEventArgs e)
    {
        string symbol = SearchBox.Text?.Trim().ToUpper() ?? "";

        if (string.IsNullOrWhiteSpace(symbol))
        {
            StatusText.Text = "Enter a stock symbol.";
            return;
        }

        await LoadStockAsync(symbol);
    }

    // =========================================================
    // LOAD STOCK
    // =========================================================

    private async Task LoadStockAsync(string symbol)
    {
        try
        {
            StatusText.Text = $"Loading {symbol}...";

            string quoteUrl = "https://www.alphavantage.co/query" +
                               "?function=GLOBAL_QUOTE" +
                               $"&symbol={Uri.EscapeDataString(symbol)}" +
                               $"&apikey={_apiKey}";

            string quoteJson = await _httpClient.GetStringAsync(quoteUrl);

            using JsonDocument quoteDocument = JsonDocument.Parse(quoteJson);

            if (!quoteDocument.RootElement.TryGetProperty("Global Quote", out JsonElement quote))
            {
                StatusText.Text = "No quote data returned.";
                return;
            }

            // Read Quote
            string price = GetValue(quote, "05. price");
            string open = GetValue(quote, "02. open");
            string high = GetValue(quote, "03. high");
            string low = GetValue(quote, "04. low");
            string previousClose = GetValue(quote, "08. previous close");
            string volume = GetValue(quote, "06. volume");
            string change = GetValue(quote, "09. change");
            string changePercent = GetValue(quote, "10. change percent");

            // Update UI
            StockSymbol.Text = symbol;
            StockName.Text = symbol;
            StockPrice.Text = FormatPrice(price);
            OpenText.Text = FormatPrice(open);
            HighText.Text = FormatPrice(high);
            LowText.Text = FormatPrice(low);
            PreviousCloseText.Text = FormatPrice(previousClose);
            VolumeText.Text = volume;
            ChangeText.Text = change;
            ChangePercentText.Text = changePercent;

            // Apply Price Color
            bool negative = change.Trim().StartsWith("-");

            IBrush priceColour = negative
                ? new SolidColorBrush(Color.Parse("#F44747"))
                : new SolidColorBrush(Color.Parse("#4EC9B0"));

            StockPrice.Foreground = priceColour;
            ChangeText.Foreground = priceColour;
            ChangePercentText.Foreground = priceColour;

            StatusText.Text = $"{symbol} loaded successfully";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
    }

    // =========================================================
    // HELPERS
    // =========================================================

    private static string GetValue(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out JsonElement value))
        {
            return value.GetString() ?? "--";
        }

        return "--";
    }

    private static string FormatPrice(string value)
    {
        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out double number))
        {
            return number.ToString("0.00", CultureInfo.InvariantCulture);
        }

        return value;
    }
}