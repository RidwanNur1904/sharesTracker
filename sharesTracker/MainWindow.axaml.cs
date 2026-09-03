using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using YahooFinanceApi;

namespace sharesTracker;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _clockTimer;
    private readonly HttpClient _httpClient = new();

    private string? _newsUrl1;
    private string? _newsUrl2;

    // Cross-platform time zone identifiers (Windows / IANA)
    private static readonly TimeZoneInfo NyTz =
        GetZone("Eastern Standard Time", "America/New_York");

    private static readonly TimeZoneInfo LondonTz =
        GetZone("GMT Standard Time", "Europe/London");

    private static readonly TimeZoneInfo FrankfurtTz =
        GetZone("W. Europe Standard Time", "Europe/Berlin");

    private static readonly TimeZoneInfo TokyoTz =
        GetZone("Tokyo Standard Time", "Asia/Tokyo");

    private static readonly TimeZoneInfo HkTz =
        GetZone("China Standard Time", "Asia/Hong_Kong");


    public MainWindow()
    {
        InitializeComponent();

        StocksList.SelectedIndex = 0;

        // Configure default User-Agent for HttpClient to mimic a web browser
        // Yahoo Finance blocks programmatic requests without standard browser headers.
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        // Initialize clock timer
        _clockTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };

        _clockTimer.Tick += ClockTimer_Tick;
        _clockTimer.Start();

        // Initial clock update immediately
        UpdateMarketClocks();

        Loaded += async (_, _) =>
        {
            await LoadStockAsync("AAPL");
        };
    }


    // =========================================================
    // EXCHANGE CLOCKS & MARKET HOURS LOGIC
    // =========================================================

    private void ClockTimer_Tick(object? sender, EventArgs e)
    {
        UpdateMarketClocks();
    }


    private void UpdateMarketClocks()
    {
        DateTime utcNow = DateTime.UtcNow;

        // NYSE / NASDAQ: 09:30 - 16:00
        UpdateExchangeUI(
            utcNow,
            NyTz,
            ClockNyse,
            StatusNyse,
            new TimeSpan(9, 30, 0),
            new TimeSpan(16, 0, 0));

        // LSE: 08:00 - 16:30
        UpdateExchangeUI(
            utcNow,
            LondonTz,
            ClockLse,
            StatusLse,
            new TimeSpan(8, 0, 0),
            new TimeSpan(16, 30, 0));

        // Frankfurt: 09:00 - 17:30
        UpdateExchangeUI(
            utcNow,
            FrankfurtTz,
            ClockFrankfurt,
            StatusFrankfurt,
            new TimeSpan(9, 0, 0),
            new TimeSpan(17, 30, 0));

        // TSE Tokyo: 09:00 - 11:30 and 12:30 - 15:30
        UpdateExchangeUI(
            utcNow,
            TokyoTz,
            ClockTse,
            StatusTse,
            new TimeSpan(9, 0, 0),
            new TimeSpan(15, 30, 0),
            new TimeSpan(11, 30, 0),
            new TimeSpan(12, 30, 0));

        // HKEX: 09:30 - 12:00 and 13:00 - 16:00
        UpdateExchangeUI(
            utcNow,
            HkTz,
            ClockHkex,
            StatusHkex,
            new TimeSpan(9, 30, 0),
            new TimeSpan(16, 0, 0),
            new TimeSpan(12, 0, 0),
            new TimeSpan(13, 0, 0));
    }


    private static void UpdateExchangeUI(
        DateTime utcNow,
        TimeZoneInfo timeZone,
        TextBlock clockBlock,
        TextBlock statusBlock,
        TimeSpan openTime,
        TimeSpan closeTime,
        TimeSpan? lunchStart = null,
        TimeSpan? lunchEnd = null)
    {
        DateTime localTime =
            TimeZoneInfo.ConvertTimeFromUtc(utcNow, timeZone);

        clockBlock.Text =
            localTime.ToString("HH:mm:ss");

        bool isWeekend =
            localTime.DayOfWeek == DayOfWeek.Saturday ||
            localTime.DayOfWeek == DayOfWeek.Sunday;

        TimeSpan timeOfDay =
            localTime.TimeOfDay;

        bool isOpen =
            !isWeekend &&
            timeOfDay >= openTime &&
            timeOfDay <= closeTime;

        // Deduct lunch break window if applicable
        if (isOpen &&
            lunchStart.HasValue &&
            lunchEnd.HasValue)
        {
            if (timeOfDay >= lunchStart.Value &&
                timeOfDay < lunchEnd.Value)
            {
                isOpen = false;
            }
        }

        if (isOpen)
        {
            statusBlock.Text = "OPEN";

            statusBlock.Foreground =
                new SolidColorBrush(
                    Color.Parse("#4ADE80"));
        }
        else
        {
            statusBlock.Text = "CLOSED";

            statusBlock.Foreground =
                new SolidColorBrush(
                    Color.Parse("#F87171"));
        }
    }


    private static TimeZoneInfo GetZone(
        string windowsId,
        string ianaId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(
                windowsId);
        }
        catch (TimeZoneNotFoundException)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(
                    ianaId);
            }
            catch
            {
                return TimeZoneInfo.Utc;
            }
        }
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
    // SEARCH WITH AUTO-RESOLVE FOR INTERNATIONAL TICKERS
    // =========================================================

    private async void Search_Click(
        object? sender,
        RoutedEventArgs e)
    {
        string input =
            SearchBox.Text?
                .Trim()
                .ToUpperInvariant() ?? "";

        if (string.IsNullOrWhiteSpace(input))
        {
            StatusText.Text =
                "Enter a stock symbol or company name.";

            return;
        }

        // Try resolving symbol/company query via Yahoo Search API first
        string resolvedSymbol = await ResolveSymbolAsync(input);

        await LoadStockAsync(resolvedSymbol);
    }

    /// <summary>
    /// Queries Yahoo Finance Search API to map raw inputs or non-US tickers to their proper exchange ticker.
    /// Example: "LLOY" -> "LLOY.L"
    /// </summary>
    private async Task<string> ResolveSymbolAsync(string input)
    {
        try
        {
            string searchUrl = $"https://query2.finance.yahoo.com/v1/finance/search?q={Uri.EscapeDataString(input)}&quotesCount=1&newsCount=0";

            using HttpResponseMessage response = await _httpClient.GetAsync(searchUrl);
            if (!response.IsSuccessStatusCode)
            {
                return input;
            }

            string json = await response.Content.ReadAsStringAsync();
            using JsonDocument doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("quotes", out JsonElement quotes) &&
                quotes.ValueKind == JsonValueKind.Array &&
                quotes.GetArrayLength() > 0)
            {
                JsonElement firstQuote = quotes[0];
                if (firstQuote.TryGetProperty("symbol", out JsonElement symbolElem))
                {
                    string? matchedSymbol = symbolElem.GetString();
                    if (!string.IsNullOrWhiteSpace(matchedSymbol))
                    {
                        return matchedSymbol;
                    }
                }
            }
        }
        catch
        {
            // Fall back to original input if search API query fails
        }

        return input;
    }


    // =========================================================
    // LOAD STOCK - YAHOO FINANCE
    // =========================================================

    private async Task LoadStockAsync(
        string symbol)
    {
        try
        {
            StatusText.Text =
                $"Loading {symbol}...";

            // Query Yahoo Finance directly through YahooFinanceApi
            var securities =
                await Yahoo
                    .Symbols(symbol)
                    .Fields(
                        Field.Symbol,
                        Field.LongName,
                        Field.ShortName,
                        Field.Currency,
                        Field.RegularMarketPrice,
                        Field.RegularMarketOpen,
                        Field.RegularMarketDayHigh,
                        Field.RegularMarketDayLow,
                        Field.RegularMarketPreviousClose,
                        Field.RegularMarketVolume,
                        Field.RegularMarketChange,
                        Field.RegularMarketChangePercent)
                    .QueryAsync();

            Security? stock = null;
            bool foundQuote = securities.TryGetValue(symbol, out stock) &&
                              GetDecimal(stock, Field.RegularMarketPrice) != null;

            // Fallback for raw UK tickers: if symbol has no extension and wasn't found, try appending ".L"
            if (!foundQuote && !symbol.Contains('.'))
            {
                string ukSymbol = $"{symbol}.L";
                StatusText.Text = $"Checking LSE exchange ({ukSymbol})...";

                var ukSecurities = await Yahoo
                    .Symbols(ukSymbol)
                    .Fields(
                        Field.Symbol,
                        Field.LongName,
                        Field.ShortName,
                        Field.Currency,
                        Field.RegularMarketPrice,
                        Field.RegularMarketOpen,
                        Field.RegularMarketDayHigh,
                        Field.RegularMarketDayLow,
                        Field.RegularMarketPreviousClose,
                        Field.RegularMarketVolume,
                        Field.RegularMarketChange,
                        Field.RegularMarketChangePercent)
                    .QueryAsync();

                if (ukSecurities.TryGetValue(ukSymbol, out stock) &&
                    GetDecimal(stock, Field.RegularMarketPrice) != null)
                {
                    symbol = ukSymbol;
                    foundQuote = true;
                }
            }

            if (!foundQuote || stock == null)
            {
                StatusText.Text =
                    $"No quote data found for {symbol}. Try adding exchange suffix (e.g., {symbol}.L for LSE).";

                return;
            }


            // -------------------------------------------------
            // Read Yahoo Finance quote
            // -------------------------------------------------

            string currency =
                GetString(
                    stock,
                    Field.Currency);

            decimal? price =
                GetDecimal(
                    stock,
                    Field.RegularMarketPrice);

            decimal? open =
                GetDecimal(
                    stock,
                    Field.RegularMarketOpen);

            decimal? high =
                GetDecimal(
                    stock,
                    Field.RegularMarketDayHigh);

            decimal? low =
                GetDecimal(
                    stock,
                    Field.RegularMarketDayLow);

            decimal? previousClose =
                GetDecimal(
                    stock,
                    Field.RegularMarketPreviousClose);

            decimal? change =
                GetDecimal(
                    stock,
                    Field.RegularMarketChange);

            decimal? changePercent =
                GetDecimal(
                    stock,
                    Field.RegularMarketChangePercent);

            long? volume =
                GetLong(
                    stock,
                    Field.RegularMarketVolume);


            string name =
                GetString(
                    stock,
                    Field.LongName);

            if (name == "--")
            {
                name =
                    GetString(
                        stock,
                        Field.ShortName);
            }

            if (name == "--")
            {
                name = symbol;
            }


            // -------------------------------------------------
            // Update UI
            // -------------------------------------------------

            StockSymbol.Text =
                symbol;

            StockName.Text =
                name;

            StockPrice.Text =
                FormatDecimal(price, currency);

            OpenText.Text =
                FormatDecimal(open, currency);

            HighText.Text =
                FormatDecimal(high, currency);

            LowText.Text =
                FormatDecimal(low, currency);

            PreviousCloseText.Text =
                FormatDecimal(previousClose, currency);

            VolumeText.Text =
                volume?.ToString(
                    CultureInfo.InvariantCulture) ?? "--";

            ChangeText.Text =
                FormatDecimal(change, currency);

            ChangePercentText.Text =
                changePercent.HasValue
                    ? $"{changePercent.Value:0.00}%"
                    : "--";


            // -------------------------------------------------
            // Apply price colour
            // -------------------------------------------------

            bool negative =
                change.HasValue &&
                change.Value < 0;

            IBrush priceColour =
                negative
                    ? new SolidColorBrush(
                        Color.Parse("#F44747"))
                    : new SolidColorBrush(
                        Color.Parse("#4EC9B0"));

            StockPrice.Foreground =
                priceColour;

            ChangeText.Foreground =
                priceColour;

            ChangePercentText.Foreground =
                priceColour;


            StatusText.Text =
                $"{symbol} loaded successfully";


            // Load news separately
            _ = LoadNewsAsync(symbol);
        }
        catch (Exception ex)
        {
            StatusText.Text =
                $"Error loading {symbol}: {ex.Message}";
        }
    }


    // =========================================================
    // NEWS (OPTION 1 - DIRECT YAHOO FINANCE WITH HEADERS)
    // =========================================================

    private async Task LoadNewsAsync(
        string symbol)
    {
        try
        {
            NewsHeadline1Title.Text =
                "Loading news...";

            NewsHeadline1Meta.Text = "";

            NewsHeadline2Title.Text = "";

            NewsHeadline2Meta.Text = "";

            NewsHeadline2Panel.IsVisible =
                true;

            _newsUrl1 = null;
            _newsUrl2 = null;


            // Yahoo Search/News Endpoint (Works keyless like yfinance)
            string url =
                "https://query2.finance.yahoo.com/v1/finance/search" +
                $"?q={Uri.EscapeDataString(symbol)}" +
                "&newsCount=2" +
                "&quotesCount=0";


            using HttpResponseMessage response =
                await _httpClient.GetAsync(url);

            response.EnsureSuccessStatusCode();


            string json =
                await response.Content.ReadAsStringAsync();

            using JsonDocument document =
                JsonDocument.Parse(json);


            if (!document.RootElement.TryGetProperty(
                    "news",
                    out JsonElement news) ||
                news.ValueKind != JsonValueKind.Array ||
                news.GetArrayLength() == 0)
            {
                NewsHeadline1Title.Text =
                    "No recent news found.";

                NewsHeadline2Panel.IsVisible =
                    false;

                return;
            }


            JsonElement[] articles =
                news.EnumerateArray()
                    .Take(2)
                    .ToArray();


            SetYahooHeadline(
                articles,
                0,
                NewsHeadline1Title,
                NewsHeadline1Meta,
                urlStr =>
                    _newsUrl1 = urlStr);


            SetYahooHeadline(
                articles,
                1,
                NewsHeadline2Title,
                NewsHeadline2Meta,
                urlStr =>
                    _newsUrl2 = urlStr);


            NewsHeadline2Panel.IsVisible =
                articles.Length > 1;
        }
        catch (Exception ex)
        {
            NewsHeadline1Title.Text =
                "News unavailable.";

            NewsHeadline1Meta.Text =
                ex.Message;

            NewsHeadline2Panel.IsVisible =
                false;
        }
    }


    private static void SetYahooHeadline(
        JsonElement[] articles,
        int index,
        TextBlock titleBlock,
        TextBlock metaBlock,
        Action<string> storeUrl)
    {
        if (index >= articles.Length)
        {
            titleBlock.Text = "";
            metaBlock.Text = "";
            return;
        }


        JsonElement article =
            articles[index];


        string title =
            GetJsonString(
                article,
                "title");

        string publisher =
            GetJsonString(
                article,
                "publisher");

        string url =
            GetJsonString(
                article,
                "link");


        long timestamp =
            GetJsonInt64(
                article,
                "providerPublishTime");


        titleBlock.Text =
            title == "--"
                ? "Untitled"
                : title;


        string time =
            timestamp > 0
                ? DateTimeOffset
                    .FromUnixTimeSeconds(timestamp)
                    .ToLocalTime()
                    .ToString(
                        "dd MMM yyyy, HH:mm",
                        CultureInfo.InvariantCulture)
                : "--";


        metaBlock.Text =
            $"{publisher} · {time}";


        if (url != "--")
        {
            storeUrl(url);
        }
    }


    // =========================================================
    // NEWS CLICK HANDLERS
    // =========================================================

    private void NewsHeadline1_Click(
        object? sender,
        Avalonia.Input.PointerPressedEventArgs e)
    {
        OpenUrl(_newsUrl1);
    }


    private void NewsHeadline2_Click(
        object? sender,
        Avalonia.Input.PointerPressedEventArgs e)
    {
        OpenUrl(_newsUrl2);
    }


    private static void OpenUrl(
        string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(
                new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
        }
        catch
        {
            // Ignore failures opening browser
        }
    }


    // =========================================================
    // YAHOO FINANCE HELPERS
    // =========================================================

    private static decimal? GetDecimal(
        Security security,
        Field field)
    {
        try
        {
            object? value =
                security[field];

            if (value == null)
            {
                return null;
            }

            return Convert.ToDecimal(
                value,
                CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }


    private static long? GetLong(
        Security security,
        Field field)
    {
        try
        {
            object? value =
                security[field];

            if (value == null)
            {
                return null;
            }

            return Convert.ToInt64(
                value,
                CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }


    private static string GetString(
        Security security,
        Field field)
    {
        try
        {
            object? value =
                security[field];

            return value?.ToString() ?? "--";
        }
        catch
        {
            return "--";
        }
    }


    private static string FormatDecimal(
        decimal? value,
        string? currency = null)
    {
        if (!value.HasValue)
        {
            return "--";
        }

        string formattedValue = value.Value.ToString(
            "0.00",
            CultureInfo.InvariantCulture);

        if (!string.IsNullOrWhiteSpace(currency) && currency != "--")
        {
            return $"{currency} {formattedValue}";
        }

        return formattedValue;
    }


    // =========================================================
    // JSON HELPERS FOR YAHOO NEWS
    // =========================================================

    private static string GetJsonString(
        JsonElement element,
        string property)
    {
        if (!element.TryGetProperty(
                property,
                out JsonElement value))
        {
            return "--";
        }

        if (value.ValueKind !=
            JsonValueKind.String)
        {
            return "--";
        }

        return value.GetString() ?? "--";
    }


    private static long GetJsonInt64(
        JsonElement element,
        string property)
    {
        if (!element.TryGetProperty(
                property,
                out JsonElement value))
        {
            return 0;
        }

        if (value.ValueKind ==
            JsonValueKind.Number &&
            value.TryGetInt64(
                out long number))
        {
            return number;
        }

        return 0;
    }

   
        private void SupplyChain_Click(object? sender, RoutedEventArgs e)
            {
                // Get the stock currently displayed on the MainWindow
                string symbol = StockSymbol.Text?.Trim() ?? "";
                string companyName = StockName.Text?.Trim() ?? "";

                // Open SupplyChain using the current stock
                var supplyChainWindow = new SupplyChain(
                    symbol,
                    companyName);

                supplyChainWindow.Show();
            }


}