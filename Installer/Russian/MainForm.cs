namespace FF14AccessibilityInstaller.Russian;

public sealed class MainForm : Form
{
    private readonly TextBox journal = new()
    {
        Multiline = true, ReadOnly = true, AcceptsTab = false, ScrollBars = ScrollBars.Both,
        WordWrap = true, Dock = DockStyle.Fill, TabIndex = 0, TabStop = true,
        AccessibleName = "Журнал установки",
        AccessibleDescription = "Сообщения проверки и установки. Стрелки перемещают по тексту, Control Home и Control End — в начало и конец. Tab — к кнопкам."
    };
    private readonly Button install = MakeButton("Установить / обновить мод", 0);
    private readonly Button russify = MakeButton("Русифицировать игру и мод", 1);
    private readonly Button check = MakeButton("Проверить обновления", 2);
    private readonly Button cancel = MakeButton("Отменить", 3);
    private readonly Button close = MakeButton("Закрыть", 4);
    private readonly InstallerService service;
    private CancellationTokenSource? operation;
    private bool busy;
    private readonly string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FF14AccessibilityInstaller", "logs", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".txt");
    private bool logFailed;

    public MainForm(InstallerService? installer = null)
    {
        service = installer ?? new InstallerService();
        var version = typeof(MainForm).Assembly.GetName().Version?.ToString(3);
        Text = $"FF14 Accessibility — установщик русской версии {version}";
        AccessibleName = Text;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(860, 520);
        MinimumSize = new Size(650, 380);
        StartPosition = FormStartPosition.CenterScreen;
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Padding = new Padding(8),
            TabIndex = 1, TabStop = false
        };
        panel.Controls.AddRange([install, russify, check, cancel, close]);
        cancel.Enabled = false;
        Controls.Add(journal);
        Controls.Add(panel);
        service.Log = message =>
        {
            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke(() => Append(message));
            else Append(message);
        };
        service.Ask = question =>
        {
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            BeginInvoke(() => answer.SetResult(MessageBox.Show(this, question, "Установка vnavmesh",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes));
            return answer.Task;
        };
        install.Click += async (_, _) => await Run(installing: true);
        russify.Click += async (_, _) => await Run(installing: true, translating: true);
        check.Click += async (_, _) => await Run(installing: false);
        cancel.Click += (_, _) =>
        {
            operation?.Cancel();
            cancel.Enabled = false;
            Append("Запрошена отмена. Если запись файлов уже началась, сначала будет завершена безопасная установка.");
        };
        close.Click += (_, _) => Close();
        FormClosing += (_, e) => { if (busy) { e.Cancel = true; Append("Дождитесь завершения или нажмите «Отменить»."); } };
        Shown += async (_, _) =>
        {
            Append(Text);
            Append("Журнал автоматически сохраняется: " + logPath);
            Append("Источник русской версии: https://github.com/" + ReleaseClient.Repository + "/releases/latest");
            Append("Перед установкой закройте игру и XIVLauncher. Tab переключает журнал и кнопки; Shift+Tab — назад.");
            Append("Русский текст игры устанавливается кнопкой «Русифицировать игру и мод». Установщик сам подготовит настройки Penumbra. Для перевода нужен английский язык клиента FFXIV.");
            await Run(installing: false);
        };
    }

    private static Button MakeButton(string label, int index) => new()
    {
        Text = label, AccessibleName = label, AutoSize = true, Padding = new Padding(8, 5, 8, 5), TabIndex = index
    };

    private void Append(string message)
    {
        // Preserve the reading position when the user is browsing earlier log lines.
        var start = journal.SelectionStart;
        var length = journal.SelectionLength;
        var reading = journal.Focused && start < journal.TextLength;
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        journal.AppendText(line);
        if (!logFailed)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.AppendAllText(logPath, line, new System.Text.UTF8Encoding(true));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logFailed = true;
                journal.AppendText("Не удалось сохранить журнал в файл: " + ex.Message + Environment.NewLine);
            }
        }
        if (reading) { journal.Select(start, length); journal.ScrollToCaret(); }
    }

    private async Task Run(bool installing, bool translating = false)
    {
        if (busy) return;
        busy = true;
        install.Enabled = russify.Enabled = check.Enabled = close.Enabled = false;
        cancel.Enabled = true;
        journal.Focus();
        using var tokenSource = new CancellationTokenSource();
        operation = tokenSource;
        try
        {
            if (installing)
            {
                var result = await Task.Run(() => translating ? service.Russify(tokenSource.Token) : service.Install(tokenSource.Token));
                Append(result);
                MessageBox.Show(this, result, "Результат установки", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else await Task.Run(() => service.Check(tokenSource.Token));
        }
        catch (OperationCanceledException)
        { Append(tokenSource.IsCancellationRequested ? "Операция отменена." : "Сервер не ответил вовремя. Повторите попытку."); }
        catch (Exception ex)
        {
            Append("Ошибка: " + ex.Message);
            if (translating) Append("Русификация не завершена. Уже выполненные шаги указаны выше. Устраните причину и нажмите «Русифицировать игру и мод» снова.");
            MessageBox.Show(this, ex.Message, "Операция не завершена", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            operation = null;
            busy = false;
            install.Enabled = russify.Enabled = check.Enabled = close.Enabled = true;
            cancel.Enabled = false;
            (translating ? russify : install).Focus();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) service.Dispose();
        base.Dispose(disposing);
    }
}
