# PolyEdge V1 — Polymarket 5m Paper Trading & Wallet Research

ASP.NET Core 8 / C# solution. Visual Studio 2022 ile `PolyEdge.sln` açılır. V1, BTC/ETH/SOL 5-minute Up/Down marketlerini izler, Binance public WebSocket fiyatlarını alır, Polymarket CLOB top-of-book verisiyle **paper-only** işlemler açıp kapatır ve araştırma cüzdanlarının public fills verisini toplar.

> **Önemli:** Bu sürüm gerçek emir göndermez, private key veya cüzdan bağlantısı kullanmaz. Paper fill simülasyonu best ask'ten giriş ve best bid'den çıkış varsayar; gerçek emir kuyruğu, partial fill, latency ve settlement doğruluğunu modellemez. Market süresi bitince son görülen bid üzerinden kapatır; bu resmi settlement sonucu değildir. Strateji yalnızca deneysel bir başlangıç kuralıdır, kârlılık iddiası değildir.

## Proje yapısı

- `src/PolyEdge.Web` — ASP.NET Core 8 web uygulaması
- `Services/MarketDataService.cs` — Binance BTC/ETH/SOL bookTicker WebSocket
- `Services/PolymarketClient.cs` — Gamma market discovery, CLOB order book, Data API wallet fills
- `Services/PaperTradingWorker.cs` — paper strategy, open positions, TP/SL, wallet fill collector
- `Data/AppDbContext.cs` — EF Core + SQLite
- `Pages/Index.cshtml` — live dashboard + SignalR
- `Dockerfile`, `docker-compose.yml` — AWS/EC2 deploy

## Visual Studio ile çalıştırma

Gereksinimler:
- Visual Studio 2022 (17.8+) ve **ASP.NET and web development** workload
- .NET 8 SDK

1. `PolyEdge.sln` dosyasını Visual Studio'da aç.
2. Startup project `PolyEdge.Web` olsun.
3. `F5` veya `Ctrl+F5` ile çalıştır.
4. Açılan adresin kökünde dashboard görünür. API sağlık kontrolü `/health`.

İlk çalıştırmada `polyedge.db` SQLite dosyası uygulama klasöründe oluşturulur. Başlangıç bakiyesi ilk DB kurulumunda $100'dır. DB dosyası silinmediği sürece yeniden başlatma bakiyeyi sıfırlamaz.

## Dashboard

- Equity = cash + açık pozisyon maliyeti + unrealized PnL
- Cash balance, realized / unrealized PnL, win rate
- Açık pozisyonlar: asset, market, UP/DOWN, entry, mark, shares, invested, unrealized PnL
- Kapanan paper trades: entry/exit, PnL, yüzdelik getiri ve kapanış nedeni
- İzlenen cüzdanların public fills kayıtları (paper hesabına kopyalanmaz)
- Bot pause/resume ve paper hesabını $100'a sıfırlama

## V1 paper strateji

- Binance `bookTicker` mid-price örnekleri arasında 12 saniyelik momentum hesaplanır.
- Mutlak momentum `Trading:MinMomentumPct` eşiğini aşarsa UP veya DOWN seçilir.
- Seçilen outcome token'ın Polymarket best ask'i `MinEntryPrice`–`MaxEntryPrice` aralığındaysa paper giriş yapılır.
- Varsayılan pozisyon maliyeti $5, eşzamanlı açık pozisyon limiti 3.
- Pozisyon best bid üzerinden mark edilir; +%12 take-profit, -%18 stop-loss veya market end'de kapatılır.
- Her market için bir 5 dakikalık periyot içinde en fazla bir giriş yapılır.

Bu kurallar başlangıç baseline'ıdır. Wallet reverse engineering sonucu çıkmadan başarılı bot stratejisi olarak kabul edilmemelidir.

## Ayarlar

`src/PolyEdge.Web/appsettings.json` içindeki `Trading` bölümü:

| Ayar | Varsayılan |
|---|---:|
| StartingBalance | 100 USD |
| RiskPerTradeUsd | 5 USD |
| MaxOpenPositions | 3 |
| MinMomentumPct | 0.00015 (0.015%) |
| MinEntryPrice | 0.25 |
| MaxEntryPrice | 0.72 |
| TakeProfitPct | 0.12 |
| StopLossPct | 0.18 |
| ScanIntervalSeconds | 5 |
| Enabled | true |

Wallet adresleri aynı dosyada `Wallets` listesinde tanımlıdır. Bu cüzdanların public fills kayıtları dakikada bir alınır. Data API yalnızca gerçekleşmiş işlemleri verdiğinden, historical order placement/cancel ve order queue davranışı bu sürümde toplanmaz.

## Local Docker

Proje kökünde:

```bash
docker compose up -d --build
```

Dashboard: `http://localhost:8080`  
Health: `http://localhost:8080/health`

Loglar:

```bash
docker compose logs -f polyedge
```

## GitHub'a gönderme

GitHub'da boş bir repository oluşturduktan sonra proje kökünde:

```bash
git init
git add .
git commit -m "Initial PolyEdge paper trading bot"
git branch -M main
git remote add origin https://github.com/YOUR-USER/PolyEdge.git
git push -u origin main
```

`polyedge.db`, `.env`, API key veya secret dosyalarını Git'e ekleme. Bu projede private API key yoktur.

## AWS EC2 (Ubuntu) deploy

Öneri: Ubuntu 24.04 EC2, Docker Engine + Compose plugin, kalıcı EBS-backed Docker volume.

1. EC2 Security Group'ta SSH (22) erişimini yalnızca kendi IP'ne aç. 8080 portunu internete doğrudan açmak yerine Nginx + HTTPS kullan; dashboard V1'de kullanıcı kimlik doğrulaması yoktur.
2. EC2'ye bağlan ve Docker kur:

```bash
sudo apt-get update
sudo apt-get install -y ca-certificates curl git
sudo install -m 0755 -d /etc/apt/keyrings
sudo curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
sudo chmod a+r /etc/apt/keyrings/docker.asc
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo $VERSION_CODENAME) stable" | sudo tee /etc/apt/sources.list.d/docker.list > /dev/null
sudo apt-get update
sudo apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
```

3. Repository'yi klonla ve başlat:

```bash
git clone https://github.com/YOUR-USER/PolyEdge.git
cd PolyEdge
sudo docker compose up -d --build
sudo docker compose logs -f polyedge
```

4. EC2 üzerinde `http://EC2-PUBLIC-IP:8080` ile test etmek için geçici olarak 8080 ingress kuralını yalnızca kendi IP adresine açabilirsin. Kalıcı kullanımda DNS + Nginx/Caddy + TLS ve erişim koruması ekle.

5. Güncelleme:

```bash
git pull
sudo docker compose up -d --build
```

SQLite verisi `polyedge_data` named volume içinde kalır. Container yeniden oluşturulsa da volume silinmediği sürece paper hesap geçmişi korunur. Yedek almak için volume snapshot veya düzenli DB backup planla.

## API endpoints

- `GET /health`
- `GET /api/dashboard`
- `GET /api/wallet-fills`
- `POST /api/trading/toggle`
- `POST /api/trading/reset`
- SignalR: `/hubs/dashboard`

## V1 sınırlamaları / sonraki geliştirme adımları

1. Market discovery slug'ında `5m` / `5-minute` tanımlayıcısı arar. Gamma API şema/slug değişirse filtre güncellenmelidir.
2. 5m market settlement sonucu henüz resmi resolution API'sinden alınmıyor. Süre sonunda son best bid ile paper close yapılır; bu settlement PnL değildir.
3. Paper emirleri gerçek order queue ve fill latency simüle etmez.
4. Wallet tracker public fills'i saklar; market bazında cüzdan PnL attribution, maker/taker ve realized/unrealized pozisyon rekonstrüksiyonu sonraki modüldür.
5. SQLite tek instance için uygundur. Birden fazla app replica veya yüksek hacim için PostgreSQL + migration ve distributed coordination gerekir.
6. Dashboard authentication/authorization V1'e dahil değildir. İnternete açık bırakma.
7. Uygulama `EnsureCreated` kullanır; production şema değişikliklerinde EF Core migrations eklenmelidir.

## Veri kaynakları

- Polymarket Gamma API: `https://gamma-api.polymarket.com/markets`
- Polymarket CLOB order book: `https://clob.polymarket.com/book?token_id=...`
- Polymarket Data API trades: `https://data-api.polymarket.com/trades?user=...`
- Binance public WebSocket: `wss://stream.binance.com:9443/stream?streams=btcusdt@bookTicker/ethusdt@bookTicker/solusdt@bookTicker`
