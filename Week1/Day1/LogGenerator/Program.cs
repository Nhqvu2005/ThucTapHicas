using System.Diagnostics;
using System.Text;

string[] Levels = { "INFO", "WARN", "ERROR", "DEBUG", "FATAL" };
string[] Users  = { "admin", "user01", "guest", "system", "root",
                    "dev01", "qa02", "svc_backup", "svc_monitor",
                    "an.nguyen", "bich.tran", "cuong.le" };

// Cac tu voi tan suat khac nhau - tu dung dau la 'connection', 'timeout' (~12% moi tu)
string[][] Messages =
{
    // NHIEU (~8-12%) - 'connection', 'timeout' duoc uu tien hon
    new[] { "connection", "timeout", "retry", "established" },
    new[] { "request", "response", "payload", "endpoint" },
    new[] { "database", "query", "insert", "select" },
    // TRUNG BINH (~3-5%)
    new[] { "authentication", "token", "expired", "refresh" },
    new[] { "disk", "memory", "cache", "eviction" },
    new[] { "queue", "worker", "thread", "pool" },
    // HIEM (~0.5-1%)
    new[] { "checksum", "mismatch", "rollback", "deadlock" },
    new[] { "replication", "lag", "snapshot", "consistency" },
    // RAT HIEM (~0.05%)
    new[] { "corruption", "segfault" },
};

string[] Actions =
{
    "GET /api/v1/orders", "POST /api/v1/orders",
    "GET /api/v1/products", "PUT /api/v1/users/{0}",
    "DELETE /api/v1/sessions", "POST /api/v1/auth/login",
    "GET /api/v1/reports/daily", "POST /api/v1/batch/process",
};

Random _rnd = new();
long _lineCount = 0;

string Pick(string[] arr) => arr[_rnd.Next(arr.Length)];
int Range(int lo, int hi) => _rnd.Next(lo, hi + 1);

string RandomWord()
{
    // 60% NHIEU, 25% TRUNG BINH, 10% HIEM, 5% RAT HIEM
    int r = _rnd.Next(100);
    var group = r switch
    {
        < 60 => Messages[0],        // NHIEU
        < 85 => Messages[1],        // TRUNG BINH
        < 95 => Messages[2],        // HIEM
        _    => Messages[3],        // RAT HIEM
    };
    int idx = _rnd.Next(group.Length);
    // Trong NHIEU: 70% chon 2 tu dau
    if (r < 60 && _rnd.Next(100) < 70)
        idx = _rnd.Next(2); // 'connection' hoac 'timeout'
    return group[idx];
}

string BuildLine()
{
    var now = DateTime.Now;
    var ts = now.AddSeconds(-_rnd.Next(86400 * 7))
                .ToString("yyyy-MM-dd HH:mm:ss.fff");
    var level = Pick(Levels);
    var user = Pick(Users);
    var ip = $"{Range(10, 223)}.{Range(0, 255)}.{Range(0, 255)}.{Range(1, 254)}";
    var action = Pick(Actions);

    var sb = new StringBuilder();
    sb.AppendFormat("{0} | {1,-5} | {2,-15} | {3,-20} | {4}",
        ts, level, ip, user, action);

    // 10-20 tu noi dung moi dong
    int wordCount = Range(10, 20);
    sb.Append(" | ");
    for (int i = 0; i < wordCount; i++)
    {
        if (i > 0) sb.Append(' ');
        sb.Append(RandomWord());
    }

    sb.AppendFormat(" | req_{0:x8}", _rnd.Next());
    return sb.ToString();
}

string FormatSize(long bytes) =>
    bytes < 1024 ? $"{bytes} B" :
    bytes < 1024L * 1024 ? $"{bytes / 1024.0:F1} KB" :
    bytes < 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024):F1} MB" :
    $"{bytes / (1024.0 * 1024 * 1024):F2} GB";

// ===== MAIN =====
Console.OutputEncoding = Encoding.UTF8;

if (args.Length > 0 && (args[0] == "-h" || args[0] == "--help"))
{
    Console.WriteLine(@"
SINH FILE LOG TEST - Dem tan suat tu
Usage: dotnet run -- [size_mb] [output_path]

Tham so:
  size_mb      Kich thuoc file (MB). Mac dinh: 5120 (~5GB)
  output_path  Duong dan file.    Mac dinh: log_5gb.txt

Vi du:
  dotnet run -- 100              -> log 100MB
  dotnet run -- 2000 data.log    -> log 2GB

Dac diem du lieu:
  - Tu 'connection' va 'timeout' xuat hien nhieu nhat (~12% moi tu)
  - Tu hiem 'corruption' chi ~0.05%
  - Moi dong ~200-400 bytes, ~2500-5000 dong/MB
");
    return;
}

long targetMB = args.Length > 0 ? long.Parse(args[0]) : 5120;
string outputPath = args.Length > 1 ? args[1] : "log_5gb.txt";

if (targetMB <= 0) { Console.WriteLine("Kich thuoc phai > 0"); return; }
if (File.Exists(outputPath))
{
    Console.Write($"  {outputPath} da ton tai. Ghi de? (y/N): ");
    if (Console.ReadLine()?.Trim().ToLower() != "y") { Console.WriteLine("Huy."); return; }
}

long targetBytes = targetMB * 1024L * 1024L;
int bufferSize = 256 * 1024;

Console.WriteLine($"\nBAT DAU SINH FILE LOG");
Console.WriteLine($"  Duong dan: {Path.GetFullPath(outputPath)}");
Console.WriteLine($"  Muc tieu:  {FormatSize(targetBytes)} ({targetMB} MB)");
Console.WriteLine($"  Buffer:    {bufferSize / 1024} KB\n");

var sw = Stopwatch.StartNew();
long written = 0;
_lineCount = 0;
var lastReport = DateTime.Now;

using (var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write,
       FileShare.None, bufferSize, FileOptions.SequentialScan))
using (var writer = new StreamWriter(fs, Encoding.UTF8, bufferSize))
{
    while (written < targetBytes)
    {
        var line = BuildLine();
        writer.WriteLine(line);
        written += line.Length + 2; // + CRLF
        _lineCount++;

        if ((DateTime.Now - lastReport).TotalSeconds >= 3)
        {
            var elapsed = sw.Elapsed;
            double speed = written / 1024.0 / 1024.0 / elapsed.TotalSeconds;
            double pct = (double)written / targetBytes * 100;
            long remainBytes = targetBytes - written;
            var eta = speed > 0
                ? TimeSpan.FromSeconds(remainBytes / 1024.0 / 1024.0 / speed)
                : TimeSpan.Zero;
            Console.Write($"\r  {pct,5:F1}% | {FormatSize(written),8} | {_lineCount,8:N0} dong | {speed,5:F0} MB/s | ETA {eta:mm\\:ss}  ");
            lastReport = DateTime.Now;
        }
    }
}

sw.Stop();
var actualSize = new FileInfo(outputPath).Length;
Console.WriteLine($"\n\nHOAN THANH!");
Console.WriteLine($"  File:       {Path.GetFullPath(outputPath)}");
Console.WriteLine($"  Kich thuoc: {FormatSize(actualSize)} ({actualSize:N0} bytes)");
Console.WriteLine($"  Tong dong:  {_lineCount,12:N0}");
Console.WriteLine($"  Thoi gian:  {sw.Elapsed.TotalSeconds:F1}s");
Console.WriteLine($"  Toc do:     {actualSize / 1024.0 / 1024.0 / sw.Elapsed.TotalSeconds:F0} MB/s\n");