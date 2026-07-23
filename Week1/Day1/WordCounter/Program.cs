// WordCounter - Dem tan suat tu trong file log (ho tro file 5GB+, ca don luong va da luong)
// Chay: dotnet run -- <file_path> [options]
// VD:   dotnet run -- log_5gb.txt -n 20
//       dotnet run -- log_5gb.txt -p -t 4 --csv

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

partial class Program
{
    [GeneratedRegex(@"[a-zA-Z_][a-zA-Z0-9_]*")]
    private static partial Regex WordPattern();

    // ===== CAU HINH =====
    static string s_filePath = "";
    static int s_topN = 10;
    static bool s_parallel = false;
    static int s_threads = 0;
    static int s_bufferKB = 256;
    static bool s_showHelp = false;
    static bool s_csv = false;

    static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        ParseArgs(args);
        if (s_showHelp) { ShowHelp(); return; }

        if (!File.Exists(s_filePath))
        {
            Console.WriteLine($"Loi: Khong tim thay file '{s_filePath}'");
            return;
        }

        var fileInfo = new FileInfo(s_filePath);
        long fileSize = fileInfo.Length;

        Console.WriteLine($"\nFILE: {Path.GetFullPath(s_filePath)}");
        Console.WriteLine($"  Kich thuoc: {FormatSize(fileSize)}");
        Console.WriteLine($"  Che do:     {(s_parallel ? $"DA LUONG ({s_threads} threads)" : "DON LUONG")}");
        Console.WriteLine($"  Buffer:     {s_bufferKB} KB\n");

        var sw = Stopwatch.StartNew();
        Dictionary<string, long> result;

        if (s_parallel)
            result = CountParallel(fileSize);
        else
            result = CountSequential(fileSize);

        sw.Stop();

        // Ket qua
        long totalWords = result.Values.Sum();
        long uniqueWords = result.Count;

        Console.WriteLine($"\nTHONG KE");
        Console.WriteLine($"  Tong so tu:       {totalWords,12:N0}");
        Console.WriteLine($"  Tu duy nhat:      {uniqueWords,12:N0}");
        Console.WriteLine($"  Thoi gian:        {sw.Elapsed.TotalSeconds,8:F2}s");
        Console.WriteLine($"  Toc do:           {fileSize / 1024.0 / 1024.0 / sw.Elapsed.TotalSeconds,7:F0} MB/s");
        Console.WriteLine($"  Tu/giay:          {totalWords / sw.Elapsed.TotalSeconds,12:N0}\n");

        // Top N nhieu nhat
        Console.WriteLine($"TOP {s_topN} TU XUAT HIEN NHIEU NHAT");
        Console.WriteLine($"  {"#",2} | {"Tu",-20} | {"So lan",-10} | {"Ty le",-8}");
        Console.WriteLine($"  {new string('-', 48)}");

        int rank = 1;
        foreach (var kvp in result.OrderByDescending(kv => kv.Value).Take(s_topN))
        {
            double pct = (double)kvp.Value / totalWords * 100;
            Console.WriteLine($"  {rank,2} | {kvp.Key,-20} | {kvp.Value,10:N0} | {pct,6:F2}%");
            rank++;
        }

        // Top N it nhat
        Console.WriteLine($"\nTOP {Math.Min(s_topN, 10)} TU XUAT HIEN IT NHAT");
        Console.WriteLine($"  {"#",2} | {"Tu",-20} | {"So lan",-10}");
        Console.WriteLine($"  {new string('-', 36)}");
        rank = 1;
        foreach (var kvp in result.OrderBy(kv => kv.Value).Take(Math.Min(s_topN, 10)))
        {
            Console.WriteLine($"  {rank,2} | {kvp.Key,-20} | {kvp.Value,10:N0}");
            rank++;
        }

        // Xuat CSV
        if (s_csv)
        {
            string csvPath = Path.ChangeExtension(s_filePath, ".csv");
            using var writer = new StreamWriter(csvPath);
            writer.WriteLine("word,count,frequency_pct");
            foreach (var kvp in result.OrderByDescending(kv => kv.Value))
            {
                double pct = (double)kvp.Value / totalWords * 100;
                writer.WriteLine($"{kvp.Key},{kvp.Value},{pct:F6}");
            }
            Console.WriteLine($"\nDa xuat CSV: {csvPath}");
        }
    }

    // ========== PHAN 1: DEM DON LUONG ==========
    // doc tung dong, dem tu bang Dictionary
    static Dictionary<string, long> CountSequential(long fileSize)
    {
        var dict = new Dictionary<string, long>(4096);
        long bytesRead = 0;
        long linesRead = 0;
        var lastReport = DateTime.Now;

        using var fs = new FileStream(s_filePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, s_bufferKB * 1024, FileOptions.SequentialScan);
        using var reader = new StreamReader(fs, Encoding.UTF8);

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            CountWordsInLine(line, dict);
            bytesRead += line.Length + 2;
            linesRead++;

            if ((DateTime.Now - lastReport).TotalSeconds >= 3)
            {
                ReportProgress(bytesRead, fileSize, linesRead, dict.Count);
                lastReport = DateTime.Now;
            }
        }

        return dict;
    }

    static void CountWordsInLine(string line, Dictionary<string, long> dict)
    {
        var matches = WordPattern().Matches(line);
        foreach (Match m in matches)
        {
            string word = m.Value;
            if (word.Length > 50) continue;
            if (int.TryParse(word, out _)) continue;

            word = word.ToLowerInvariant();

            dict.TryGetValue(word, out long count);
            dict[word] = count + 1;
        }
    }

    // ========== PHAN 2: DEM DA LUONG ==========
    // dung Parallel.ForEach tren cac chunk
    // Moi chunk tu mo FileStream rieng, co dictionary rieng
    // => khong can lock khi dem, merge sau
    static Dictionary<string, long> CountParallel(long fileSize)
    {
        int threadCount = s_threads > 0 ? s_threads : Environment.ProcessorCount;
        long chunkSize = fileSize / threadCount + 1;

        // Tao danh sach chunk: (start, end)
        var chunks = new List<(long start, long end)>();
        for (int i = 0; i < threadCount; i++)
        {
            long start = i * chunkSize;
            long end = Math.Min(start + chunkSize, fileSize);
            if (start >= fileSize) break;
            chunks.Add((start, end));
        }

        var merged = new Dictionary<string, long>(4096);
        var mergeLock = new object();
        long totalRead = 0;
        long totalLines = 0;

        Console.WriteLine($"  So chunk: {chunks.Count} (chunk size ~{FormatSize(chunkSize)})\n");

        // Parallel.ForEach voi thread-local dictionary
        // localInit: tao dictionary rieng cho moi thread
        // body: xu ly chunk, tra ve dictionary
        // localFinally: gop dictionary vao ket qua chung
        Parallel.ForEach(
            chunks,
            new ParallelOptions { MaxDegreeOfParallelism = threadCount },
            () => new Dictionary<string, long>(4096),                      // localInit
            (chunk, _, localDict) =>                                          // body
            {
                using var fs = new FileStream(s_filePath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, s_bufferKB * 1024, FileOptions.SequentialScan);
                using var reader = new StreamReader(fs, Encoding.UTF8);

                fs.Seek(chunk.start, SeekOrigin.Begin);
                if (chunk.start > 0) reader.ReadLine();                              // bo dong dang do

                string? line;
                long localBytes = 0;
                long localLines = 0;

                while ((line = reader.ReadLine()) != null)
                {
                    long pos = fs.Position;
                    if (pos > chunk.end && chunk.end < fileSize) break;

                    CountWordsInLine(line, localDict);
                    localBytes += line.Length + 2;
                    localLines++;
                }

                // Cap nhat tien do
                lock (mergeLock)
                {
                    totalRead += localBytes;
                    totalLines += localLines;
                }

                return localDict;
            },
            localDict =>                                                              // localFinally
            {
                lock (mergeLock)
                {
                    foreach (var kvp in localDict)
                    {
                        merged.TryGetValue(kvp.Key, out long count);
                        merged[kvp.Key] = count + kvp.Value;
                    }
                }
            }
        );

        return merged;
    }

    // ========== TIEN ICH ==========
    static void ReportProgress(long done, long total, long lines, int uniqueWords)
    {
        double pct = (double)done / total * 100;
        double speed = done / 1024.0 / 1024.0;
        var elapsed = DateTime.Now - Process.GetCurrentProcess().StartTime;
        speed /= elapsed.TotalSeconds;

        long remain = total - done;
        var eta = speed > 0
            ? TimeSpan.FromSeconds(remain / 1024.0 / 1024.0 / speed)
            : TimeSpan.Zero;

        Console.Write($"\r  {pct,5:F1}% | {FormatSize(done),8} | {lines,10:N0} dong");
        if (uniqueWords >= 0)
            Console.Write($" | {uniqueWords,7:N0} tu duy nhat");
        Console.Write($" | {speed,5:F0} MB/s | ETA {eta:mm\\:ss}  ");
    }

    static string FormatSize(long bytes) =>
        bytes < 1024 ? $"{bytes} B" :
        bytes < 1024L * 1024 ? $"{bytes / 1024.0:F1} KB" :
        bytes < 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024):F1} MB" :
        $"{bytes / (1024.0 * 1024 * 1024):F2} GB";

    static void ParseArgs(string[] args)
    {
        if (args.Length == 0 || args[0] == "-h" || args[0] == "--help")
        {
            s_showHelp = true;
            return;
        }

        s_filePath = args[0];
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-n":
                case "--top":
                    if (++i < args.Length) s_topN = int.Parse(args[i]);
                    break;
                case "-p":
                case "--parallel":
                    s_parallel = true;
                    break;
                case "-t":
                case "--threads":
                    if (++i < args.Length) s_threads = int.Parse(args[i]);
                    break;
                case "-b":
                case "--buffer":
                    if (++i < args.Length) s_bufferKB = int.Parse(args[i]);
                    break;
                case "--csv":
                    s_csv = true;
                    break;
            }
        }

        if (s_threads <= 0) s_threads = Environment.ProcessorCount;
    }

    static void ShowHelp()
    {
        Console.WriteLine(@"
WORD COUNTER - Dem tan suat tu trong file log
================================================
Usage: dotnet run -- <file_path> [options]

Vi tri:
  file_path                    Duong dan file log can dem

Tuy chon:
  -n, --top <so>              So tu hien thi (mac dinh: 10)
  -p, --parallel              Che do da luong (Parallel.ForEach)
  -t, --threads <so>          So luong (mac dinh: so core CPU)
  -b, --buffer <KB>           Buffer doc file (mac dinh: 256 KB)
  --csv                       Xuat CSV (word, count, frequency_pct)
  -h, --help                  Xem huong dan

Vi du:
  dotnet run -- log_5gb.txt
  dotnet run -- log_5gb.txt -n 20
  dotnet run -- log_5gb.txt -p -t 8
  dotnet run -- log_5gb.txt --csv
  dotnet run -- log_5gb.txt -p -t 4 --csv
");
    }
}