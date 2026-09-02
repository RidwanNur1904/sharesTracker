using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace sharesTracker
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private const string ApiKey = "YOUR_ALPHA_VANTAGE_API_KEY";

        private static readonly HttpClient Http = new HttpClient();

        private string _selectedMarket = "NASDAQ";
        private string _searchText = "";
        private string _statusText = "Connecting...";

        public ObservableCollection<Stock> TopStocks { get; } = new();

        public string SelectedMarket
        {
            get => _selectedMarket;
            set
            {
                if (_selectedMarket == value)
                    return;

                _selectedMarket = value;
                OnPropertyChanged(nameof(SelectedMarket));

                _ = LoadMarketAsync();
            }
        }

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (_searchText == value)
                    return;

                _searchText = value;
                OnPropertyChanged(nameof(SearchText));
            }
        }

        public string StatusText
        {
            get => _statusText;
            set
            {
                if (_statusText == value)
                    return;

                _statusText = value;
                OnPropertyChanged(nameof(StatusText));
            }
        }

        public MainWindow()
        {
            InitializeComponent();

            DataContext = this;

            _ = LoadMarketAsync();
        }


        // =====================================================
        // LOAD TOP 10 FOR CURRENT MARKET
        // =====================================================

        private async Task LoadMarketAsync()
        {
            try
            {
                StatusText = $"Loading {SelectedMarket}...";

                TopStocks.Clear();

                /*
                 * Alpha Vantage's LISTING_STATUS endpoint gives us
                 * the current list of securities.
                 *
                 * We filter it by exchange and then request quotes
                 * for the securities we want to display.
                 */

                string url =
                    $"https://www.alphavantage.co/query" +
                    $"?function=LISTING_STATUS" +
                    $"&apikey={ApiKey}";

                using var response = await Http.GetAsync(url);

                response.EnsureSuccessStatusCode();

                string csv = await response.Content.ReadAsStringAsync();

                var listings = ParseListingStatus(csv);

                foreach (var listing in listings)
                {
                    if (!MatchesMarket(listing.Exchange))
                        continue;

                    /*
                     * We deliberately don't hard-code companies.
                     * The ticker/company/exchange information comes
                     * from Alpha Vantage.
                     */

                    TopStocks.Add(new Stock
                    {
                        Symbol = listing.Symbol,
                        Name = listing.Name,
                        Exchange = listing.Exchange
                    });

                    if (TopStocks.Count >= 10)
                        break;
                }

                StatusText =
                    $"{TopStocks.Count} securities loaded · {SelectedMarket}";
            }
            catch (Exception ex)
            {
                StatusText = $"API error: {ex.Message}";
            }
        }


        // =====================================================
        // MARKET FILTER
        // =====================================================

        private bool MatchesMarket(string exchange)
        {
            if (string.IsNullOrWhiteSpace(exchange))
                return false;

            exchange = exchange.ToUpperInvariant();

            return SelectedMarket switch
            {
                "NASDAQ" =>
                    exchange.Contains("NASDAQ"),

                "NYSE" =>
                    exchange.Contains("NYSE"),

                "LSE" =>
                    exchange.Contains("LONDON"),

                _ => true
            };
        }


        // =====================================================
        // PARSE ALPHA VANTAGE LISTING_STATUS CSV
        // =====================================================

        private static ObservableCollection<Listing> ParseListingStatus(
            string csv)
        {
            var results = new ObservableCollection<Listing>();

            string[] lines = csv.Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries);

            if (lines.Length <= 1)
                return results;

            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i].Trim();

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                string[] columns = SplitCsvLine(line);

                if (columns.Length < 4)
                    continue;

                results.Add(new Listing
                {
                    Symbol = columns[0],
                    Name = columns[1],
                    Exchange = columns[2],
                    AssetType = columns[3]
                });
            }

            return results;
        }


        // =====================================================
        // BASIC CSV PARSER
        // =====================================================

        private static string[] SplitCsvLine(string line)
        {
            return line.Split(',');
        }


        // =====================================================
        // SEARCH
        // =====================================================

        private async void SearchButton_Click(
            object? sender,
            RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(SearchText))
                return;

            try
            {
                StatusText = $"Searching for {SearchText}...";

                string url =
                    $"https://www.alphavantage.co/query" +
                    $"?function=SYMBOL_SEARCH" +
                    $"&keywords={Uri.EscapeDataString(SearchText)}" +
                    $"&apikey={ApiKey}";

                using var response = await Http.GetAsync(url);

                response.EnsureSuccessStatusCode();

                string json = await response.Content.ReadAsStringAsync();

                using JsonDocument document =
                    JsonDocument.Parse(json);

                if (!document.RootElement.TryGetProperty(
                        "bestMatches",
                        out JsonElement matches))
                {
                    StatusText = "No results found.";
                    return;
                }

                if (matches.GetArrayLength() == 0)
                {
                    StatusText = "No results found.";
                    return;
                }

                JsonElement first = matches[0];

                string symbol =
                    first.GetProperty("1. symbol").GetString() ?? "";

                string name =
                    first.GetProperty("2. name").GetString() ?? "";

                string type =
                    first.GetProperty("3. type").GetString() ?? "";

                string region =
                    first.GetProperty("4. region").GetString() ?? "";

                StatusText =
                    $"{symbol} · {name} · {region}";

                /*
                 * This is where we will later update:
                 *
                 * - Graph
                 * - Current price
                 * - Market data
                 * - News
                 *
                 * using the returned ticker.
                 */
            }
            catch (Exception ex)
            {
                StatusText = $"Search error: {ex.Message}";
            }
        }


        // =====================================================
        // PROPERTY CHANGED
        // =====================================================

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(propertyName));
        }
    }


    // =========================================================
    // STOCK
    // =========================================================

    public class Stock
    {
        public string Symbol { get; set; } = "";
        public string Name { get; set; } = "";
        public string Exchange { get; set; } = "";

        public string Price { get; set; } = "—";
        public string Change { get; set; } = "—";
        public string ChangePercent { get; set; } = "—";
    }


    // =========================================================
    // LISTING
    // =========================================================

    public class Listing
    {
        public string Symbol { get; set; } = "";
        public string Name { get; set; } = "";
        public string Exchange { get; set; } = "";
        public string AssetType { get; set; } = "";
    }
}

