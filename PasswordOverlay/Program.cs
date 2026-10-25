
using System.Drawing.Drawing2D;
using System.IO.Ports;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Text.Json.Serialization;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new OverlayForm());
    }
}

public class Conta
{
    public int Id { get; set; }
    public string Nome { get; set; } = "";
    public string Usuario { get; set; } = "";
    public string Senha { get; set; } = "";
    public string Descricao { get; set; } = "";
}

public class RespostaEsp32
{
    public string? tipo { get; set; }
    public bool ok { get; set; }
    public string? erro { get; set; }
    public int id { get; set; }
    public List<Conta>? contas { get; set; }
    public Conta? conta { get; set; }
    public string? mensagem { get; set; }
}

public static class CofreEsp32
{
    private static readonly JsonSerializerOptions Opcoes = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static RespostaEsp32 Enviar(
        SerialPort serial,
        object pedido,
        Action<string>? tratarBotao = null)
    {
        if (!serial.IsOpen)
            throw new InvalidOperationException(
                "ESP32 desconectado.");

        string json = JsonSerializer.Serialize(pedido);

        serial.WriteLine(json);

        var limite = DateTime.UtcNow.AddSeconds(3);

        while (DateTime.UtcNow < limite)
        {
            string linha;

            try
            {
                linha = serial.ReadLine().Trim();
            }
            catch (TimeoutException)
            {
                continue;
            }

            if (linha == "NAVEGAR" || linha == "SELECIONAR")
            {
                tratarBotao?.Invoke(linha);
                continue;
            }

            try
            {
                var resposta =
                    JsonSerializer.Deserialize<RespostaEsp32>(
                        linha, Opcoes);

                if (resposta?.tipo == "RESPOSTA")
                    return resposta;
            }
            catch (JsonException)
            {
                // Ignora mensagens que não sejam respostas JSON.
            }
        }

        throw new TimeoutException(
            "O ESP32 não respondeu ao comando.");
    }
}


public class OverlayForm : Form
{
    private readonly SerialPort serial = new("COM5", 115200);
    private readonly System.Windows.Forms.Timer timer = new();
    private List<Conta> contas = new();
    private bool inicializando;
    private readonly Panel barraTopo = new();
    private readonly Label titulo = new();
    private readonly Label subtitulo = new();
    private readonly Label status = new();
    private readonly Label indicador = new();
    private readonly FlowLayoutPanel lista = new();
    private readonly Button fechar = new();

    private Button adicionar = new();
    private Button editar = new();
    private Button excluir = new();
    private Button revelar = new();
    private Button copiar = new();

    private int indice;
    private bool arrastando;
    private Point pontoInicial;
    private bool senhaVisivel;
    private bool digitandoSenha;

    private IntPtr janelaDestino = IntPtr.Zero;

    private readonly Color fundo = Color.FromArgb(15, 17, 26);
    private readonly Color painel = Color.FromArgb(24, 27, 40);
    private readonly Color roxo = Color.FromArgb(151, 109, 255);
    private readonly Color texto = Color.FromArgb(240, 241, 250);
    private readonly Color textoSecundario = Color.FromArgb(145, 151, 173);

    // API do Windows
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(
        uint nInputs, INPUT[] pInputs, int cbSize);

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const int SW_RESTORE = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;

        [FieldOffset(0)]
        public KEYBDINPUT ki;

        [FieldOffset(0)]
        public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    private void TratarBotaoDuranteComando(string comando)
    {
        if (comando == "NAVEGAR" && contas.Count > 0)
        {
            indice = (indice + 1) % contas.Count;
            senhaVisivel = false;
            AtualizarContas();
        }
        else if (comando == "SELECIONAR" && contas.Count > 0)
        {
            _ = PreencherSenha();
        }
    }

    private bool RecarregarContas()
    {
        try
        {
            var resposta = EnviarComando(new { cmd = "LISTAR" });

            if (!resposta.ok)
            {
                status.Text = "ERRO AO LISTAR CONTAS";
                return false;
            }

            contas = resposta.contas ?? new List<Conta>();

            indice = contas.Count == 0
                ? 0
                : Math.Clamp(indice, 0, contas.Count - 1);

            senhaVisivel = false;
            AtualizarContas();

            status.Text = contas.Count > 0
                ? "CONTAS CARREGADAS DO ESP32"
                : "NENHUMA CONTA CADASTRADA";

            return true;
        }
        catch (Exception ex)
        {
            status.Text = "ERRO AO LER O ESP32";

            MessageBox.Show(
                "Não foi possível carregar as contas:\n" + ex.Message,
                "NEXUS",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);

            return false;
        }
    }

    private Conta ObterContaSelecionada()
    {
        if (!TemConta())
            throw new InvalidOperationException(
                "Nenhuma conta selecionada.");

        var resposta = EnviarComando(new
        {
            cmd = "OBTER",
            id = contas[indice].Id
        });

        if (!resposta.ok || resposta.conta == null)
            throw new InvalidOperationException(
                resposta.erro ?? "Não foi possível obter a conta.");

        return resposta.conta;
    }

    private RespostaEsp32 EnviarComando(object pedido)
    {
        if (!serial.IsOpen)
            throw new InvalidOperationException(
                "O ESP32 não está conectado à COM5.");

        return CofreEsp32.Enviar(
            serial,
            pedido,
            TratarBotaoDuranteComando);
    }

    public OverlayForm()
    {
        Size = new Size(360, 440);
        MinimumSize = Size;
        MaximumSize = Size;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = fundo;
        ForeColor = texto;
        TopMost = true;
        DoubleBuffered = true;

        try
        {
            serial.NewLine = "\n";
            serial.ReadTimeout = 100;
            serial.WriteTimeout = 1000;
            serial.Open();

            status.Text = "ESP32 CONECTADO • COM5";
            status.ForeColor = Color.FromArgb(100, 220, 170);

            serial.DiscardInBuffer();

            RecarregarContas();
        }
        catch (Exception ex)
        {
            status.Text = "ESP32 DESCONECTADO • COM5";
            status.ForeColor = Color.FromArgb(255, 120, 130);

            MessageBox.Show(
                "Erro ao conectar ao ESP32:\n" + ex.Message,
                "NEXUS",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        CriarInterface();
        AtualizarContas();

        try
        {
            serial.NewLine = "\n";
            serial.ReadTimeout = 100;
            serial.Open();

            status.Text = "ESP32 CONECTADO • COM5";
            status.ForeColor = Color.FromArgb(100, 220, 170);
        }
        catch
        {
            status.Text = "ESP32 DESCONECTADO • COM5";
            status.ForeColor = Color.FromArgb(255, 120, 130);
        }

        timer.Interval = 50;
        timer.Tick += (_, _) => LerSerial();
        timer.Start();

        Shown += (_, _) => Hide();

        FormClosing += (_, _) =>
        {
            timer.Stop();

            if (serial.IsOpen)
                serial.Close();

            serial.Dispose();
        };
    }


    private void CriarInterface()
    {
        fechar.Text = "×";
        fechar.Font = new Font("Segoe UI", 16);
        fechar.ForeColor = textoSecundario;
        fechar.BackColor = fundo;
        fechar.FlatStyle = FlatStyle.Flat;
        fechar.FlatAppearance.BorderSize = 0;
        fechar.SetBounds(316, 5, 30, 30);
        fechar.Click += (_, _) => Close();

        indicador.Font = new Font("Consolas", 9, FontStyle.Bold);
        indicador.ForeColor = roxo;
        indicador.SetBounds(18, 12, 150, 20);

        var legenda = new Label
        {
            Text = "SUAS CONTAS",
            Font = new Font("Segoe UI", 8, FontStyle.Bold),
            ForeColor = textoSecundario,
            Bounds = new Rectangle(18, 36, 300, 20)
        };

        lista.SetBounds(14, 58, 332, 208);
        lista.FlowDirection = FlowDirection.TopDown;
        lista.WrapContents = false;
        lista.AutoScroll = true;
        lista.BackColor = fundo;

        status.Font = new Font("Segoe UI", 7, FontStyle.Bold);
        status.SetBounds(18, 270, 325, 28);
        status.Text = "Cofre carregado.";

        adicionar = Botao("＋ ADICIONAR", 18, 304, 155, 34, roxo);
        editar = Botao("EDITAR", 185, 304, 157, 34, painel);
        excluir = Botao("EXCLUIR", 18, 346, 155, 34, painel);
        revelar = Botao("MOSTRAR SENHA", 185, 346, 157, 34, painel);
        copiar = Botao("COPIAR SENHA", 18, 388, 324, 32, painel);

        adicionar.Click += (_, _) => AdicionarConta();
        editar.Click += (_, _) => EditarConta();
        excluir.Click += (_, _) => ExcluirConta();
        revelar.Click += (_, _) => RevelarSenha();
        copiar.Click += (_, _) => CopiarSenha();

        Controls.AddRange(new Control[]
        {
        fechar,
        indicador,
        legenda,
        lista,
        status,
        adicionar,
        editar,
        excluir,
        revelar,
        copiar
        });

        fechar.BringToFront();
    }


    private Button Botao(
        string textoBotao, int x, int y,
        int largura, int altura, Color cor)
    {
        var botao = new Button
        {
            Text = textoBotao,
            Font = new Font("Segoe UI", 8, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = cor,
            Bounds = new Rectangle(x, y, largura, altura),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };

        botao.FlatAppearance.BorderSize = 0;
        return botao;
    }

    private void AtualizarContas()
    {
        lista.Controls.Clear();

        if (contas.Count == 0)
        {
            lista.Controls.Add(new Label
            {
                Text = "Nenhuma conta cadastrada.\nClique em ADICIONAR.",
                ForeColor = textoSecundario,
                BackColor = fundo,
                Font = new Font("Segoe UI", 9),
                Size = new Size(300, 55),
                Padding = new Padding(8)
            });

            indicador.Text = "00 / 00";
            return;
        }

        indice = Math.Clamp(indice, 0, contas.Count - 1);

        for (int i = 0; i < contas.Count; i++)
        {
            int posicao = i;
            bool ativa = i == indice;

            var cartao = new Panel
            {
                Size = new Size(305, 44),
                BackColor = ativa
                    ? Color.FromArgb(48, 38, 75)
                    : painel,
                Margin = new Padding(0, 0, 0, 6),
                Cursor = Cursors.Hand
            };

            var marcador = new Panel
            {
                Bounds = new Rectangle(0, 0, 3, 44),
                BackColor = ativa ? roxo : painel
            };

            var nome = new Label
            {
                Text = contas[i].Nome,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                ForeColor = texto,
                Location = new Point(12, 3),
                Size = new Size(270, 20),
                BackColor = Color.Transparent
            };

            var usuario = new Label
            {
                Text = contas[i].Usuario,
                Font = new Font("Segoe UI", 7),
                ForeColor = textoSecundario,
                Location = new Point(12, 23),
                Size = new Size(270, 16),
                BackColor = Color.Transparent
            };

            cartao.Controls.AddRange(new Control[]
            {
                marcador, nome, usuario
            });

            cartao.Click += (_, _) => SelecionarIndice(posicao);
            nome.Click += (_, _) => SelecionarIndice(posicao);
            usuario.Click += (_, _) => SelecionarIndice(posicao);

            lista.Controls.Add(cartao);
        }

        indicador.Text = $"{indice + 1:00} / {contas.Count:00}";
    }

    private void SelecionarIndice(int novoIndice)
    {
        indice = novoIndice;
        senhaVisivel = false;
        AtualizarContas();

        status.Text = $"CONTA: {contas[indice].Nome}";
        status.ForeColor = roxo;
    }

    private void AdicionarConta()
    {
        TopMost = false;

        try
        {
            var conta = EditarDialog.Mostrar(
                this, null, fundo, painel, roxo, texto);

            if (conta == null)
                return;

            var resposta = EnviarComando(new
            {
                cmd = "CRIAR",
                conta = new
                {
                    nome = conta.Nome,
                    usuario = conta.Usuario,
                    senha = conta.Senha,
                    descricao = conta.Descricao
                }
            });

            if (!resposta.ok)
                throw new InvalidOperationException(
                    resposta.erro ?? "Não foi possível criar a conta.");

            if (resposta.id > 0)
                conta.Id = resposta.id;

            if (RecarregarContas())
            {
                indice = contas.FindIndex(c => c.Id == conta.Id);
                if (indice < 0)
                    indice = Math.Max(0, contas.Count - 1);

                AtualizarContas();
                status.Text = "CONTA SALVA NO ESP32";
                status.ForeColor = Color.FromArgb(100, 220, 170);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Erro ao adicionar conta:\n" + ex.Message,
                "NEXUS",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            TopMost = true;
        }
    }

    private void EditarConta()
    {
        if (!TemConta())
            return;

        TopMost = false;

        try
        {
            var original = ObterContaSelecionada();

            var alterada = EditarDialog.Mostrar(
                this, original, fundo, painel, roxo, texto);

            if (alterada == null)
                return;

            var resposta = EnviarComando(new
            {
                cmd = "EDITAR",
                id = original.Id,
                conta = new
                {
                    nome = alterada.Nome,
                    usuario = alterada.Usuario,
                    senha = alterada.Senha,
                    descricao = alterada.Descricao
                }
            });

            if (!resposta.ok)
                throw new InvalidOperationException(
                    resposta.erro ?? "Não foi possível editar a conta.");

            indice = contas.FindIndex(c => c.Id == original.Id);
            if (indice < 0)
                indice = 0;

            if (RecarregarContas())
            {
                indice = contas.FindIndex(c => c.Id == original.Id);
                if (indice < 0)
                    indice = 0;

                AtualizarContas();
                status.Text = "ALTERAÇÕES SALVAS NO ESP32";
                status.ForeColor = Color.FromArgb(100, 220, 170);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Erro ao editar conta:\n" + ex.Message,
                "NEXUS",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            TopMost = true;
        }
    }

    private void ExcluirConta()
    {
        if (!TemConta())
            return;

        var conta = contas[indice];

        var respostaConfirmacao = MessageBox.Show(
            $"Excluir a conta '{conta.Nome}'?",
            "Confirmar exclusão",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (respostaConfirmacao != DialogResult.Yes)
            return;

        try
        {
            var resposta = EnviarComando(new
            {
                cmd = "EXCLUIR",
                id = conta.Id
            });

            if (!resposta.ok)
                throw new InvalidOperationException(
                    resposta.erro ?? "Não foi possível excluir a conta.");

            indice = Math.Max(0, indice - 1);

            if (RecarregarContas())
            {
                senhaVisivel = false;
                AtualizarContas();
                status.Text = "CONTA EXCLUÍDA DO ESP32";
                status.ForeColor = Color.FromArgb(100, 220, 170);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Erro ao excluir conta:\n" + ex.Message,
                "NEXUS",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private bool TemConta()
    {
        if (contas.Count > 0)
            return true;

        status.Text = "CADASTRE UMA CONTA PRIMEIRO";
        status.ForeColor = Color.Orange;
        return false;
    }

    private void RevelarSenha()
    {
        if (!TemConta())
            return;

        try
        {
            var conta = ObterContaSelecionada();

            senhaVisivel = !senhaVisivel;

            MessageBox.Show(
                $"Conta: {conta.Nome}\n" +
                $"Usuário: {conta.Usuario}\n" +
                $"Senha: {(senhaVisivel ? conta.Senha : "••••••••")}",
                "Dados da conta",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Erro ao consultar senha:\n" + ex.Message,
                "NEXUS",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void CopiarSenha()
    {
        if (!TemConta())
            return;

        try
        {
            var conta = ObterContaSelecionada();

            if (string.IsNullOrEmpty(conta.Senha))
            {
                status.Text = "A CONTA NÃO POSSUI SENHA";
                return;
            }

            Clipboard.SetText(conta.Senha);

            status.Text = "SENHA COPIADA!";
            status.ForeColor = Color.FromArgb(100, 220, 170);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Erro ao copiar senha:\n" + ex.Message,
                "NEXUS",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void LerSerial()
    {
        if (!serial.IsOpen || serial.BytesToRead == 0)
            return;

        try
        {
            while (serial.BytesToRead > 0)
            {
                string comando = serial.ReadLine().Trim();

                if (comando == "NAVEGAR" && contas.Count > 0)
                {
                    indice = (indice + 1) % contas.Count;
                    senhaVisivel = false;
                    AtualizarContas();

                    status.Text = $"CONTA: {contas[indice].Nome}";
                    status.ForeColor = roxo;

                    MostrarOverlay();
                }
                else if (comando == "SELECIONAR")
                {
                    if (contas.Count > 0)
                        _ = PreencherSenha();
                    else
                    {
                        MostrarOverlay();
                        status.Text = "CADASTRE UMA CONTA PRIMEIRO";
                    }
                }
            }
        }
        catch (TimeoutException)
        {
        }
        catch (InvalidOperationException)
        {
            status.Text = "ERRO NA COMUNICACAO SERIAL";
        }
    }

    private async Task PreencherSenha()
    {
        if (digitandoSenha || contas.Count == 0)
            return;

        digitandoSenha = true;

        try
        {
            var conta = ObterContaSelecionada();
            string senha = conta.Senha;

            IntPtr destino = janelaDestino;

            if (string.IsNullOrEmpty(senha))
            {
                status.Text = "A CONTA NÃO POSSUI SENHA";
                MostrarOverlay();
                return;
            }

            if (destino == IntPtr.Zero || destino == Handle)
            {
                status.Text = "SELECIONE A JANELA DE DESTINO";
                MostrarOverlay();
                return;
            }

            Hide();
            await Task.Delay(300);

            SetForegroundWindow(destino);
            await Task.Delay(250);

            if (GetForegroundWindow() != destino)
            {
                MostrarOverlay();
                status.Text = "NÃO FOI POSSÍVEL ATIVAR A JANELA";
                return;
            }

            await DigitarTexto(senha);
        }
        catch (Exception ex)
        {
            MostrarOverlay();

            MessageBox.Show(
                ex.Message,
                "Falha na digitação",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            digitandoSenha = false;
        }
    }


    private async Task DigitarTexto(string valor)
    {
        foreach (char caractere in valor)
        {
            INPUT[] eventos =
            {
            new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wScan = caractere,
                        dwFlags = KEYEVENTF_UNICODE
                    }
                }
            },
            new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wScan = caractere,
                        dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP
                    }
                }
            }
        };

            uint enviados = SendInput(
                (uint)eventos.Length,
                eventos,
                Marshal.SizeOf<INPUT>());

            if (enviados != eventos.Length)
            {
                int erro = Marshal.GetLastWin32Error();

                throw new InvalidOperationException(
                    $"SendInput enviou {enviados}/2 eventos. Erro Win32: {erro}.");
            }

            await Task.Delay(5);
        }
    }


    private void MostrarOverlay()
    {
        if (!Visible)
        {
            IntPtr ativa = GetForegroundWindow();

            if (ativa != IntPtr.Zero && ativa != Handle)
                janelaDestino = ativa;

            Show();
        }

        WindowState = FormWindowState.Normal;
        TopMost = true;
        Activate();
    }

    private void IniciarArrasto(object? sender, MouseEventArgs e)
    {
        arrastando = true;
        pontoInicial = e.Location;
    }

    private void MoverJanela(object? sender, MouseEventArgs e)
    {
        if (!arrastando)
            return;

        Point atual = barraTopo.PointToScreen(e.Location);

        Location = new Point(
            atual.X - pontoInicial.X,
            atual.Y - pontoInicial.Y);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        using var caneta = new Pen(Color.FromArgb(65, roxo), 1);
        e.Graphics.DrawRectangle(
            caneta, 0, 0, Width - 1, Height - 1);
    }
}


public class EditarDialog : Form
{
    private readonly TextBox nome = new();
    private readonly TextBox usuario = new();
    private readonly TextBox senha = new();
    private readonly TextBox descricao = new();

    private EditarDialog(Conta? original, Color fundo, Color painel,
        Color roxo, Color texto)
    {
        Text = "Conta";
        Size = new Size(390, 420);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = fundo;
        ForeColor = texto;

        var titulo = new Label
        {
            Text = original == null ? "NOVA CONTA" : "EDITAR CONTA",
            Font = new Font("Segoe UI", 13, FontStyle.Bold),
            ForeColor = roxo,
            Bounds = new Rectangle(20, 15, 330, 30)
        };

        AdicionarCampo("Nome da conta", nome, 55);
        AdicionarCampo("Usuário ou e-mail", usuario, 115);
        AdicionarCampo("Senha", senha, 175);
        AdicionarCampo("Descrição (opcional)", descricao, 235);

        nome.Text = original?.Nome ?? "";
        usuario.Text = original?.Usuario ?? "";
        senha.Text = original?.Senha ?? "";
        descricao.Text = original?.Descricao ?? "";

        var salvar = new Button
        {
            Text = "SALVAR CONTA",
            Bounds = new Rectangle(20, 315, 330, 42),
            BackColor = roxo,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            DialogResult = DialogResult.OK
        };
        salvar.FlatAppearance.BorderSize = 0;

        AcceptButton = salvar;

        Controls.Add(titulo);
        Controls.Add(salvar);
        Controls.AddRange(new Control[]
        {
            nome, usuario, senha, descricao
        });

        salvar.Click += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(nome.Text) ||
                string.IsNullOrWhiteSpace(usuario.Text) ||
                string.IsNullOrEmpty(senha.Text))
            {
                MessageBox.Show(
                    "Preencha nome, usuário e senha.",
                    "Campos obrigatórios",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                DialogResult = DialogResult.None;
            }
        };
    }

    private void AdicionarCampo(string legenda, TextBox campo, int y)
    {
        var label = new Label
        {
            Text = legenda,
            Bounds = new Rectangle(20, y, 330, 18),
            ForeColor = Color.FromArgb(145, 151, 173),
            Font = new Font("Segoe UI", 8)
        };

        campo.Bounds = new Rectangle(20, y + 20, 330, 28);
        campo.BackColor = Color.FromArgb(35, 38, 52);
        campo.ForeColor = Color.White;
        campo.BorderStyle = BorderStyle.FixedSingle;

        if (campo == senha)
            campo.UseSystemPasswordChar = true;

        Controls.Add(label);
    }

    public static Conta? Mostrar(IWin32Window dono, Conta? original,
        Color fundo, Color painel, Color roxo, Color texto)
    {
        using var dialogo = new EditarDialog(
            original, fundo, painel, roxo, texto);

        if (dialogo.ShowDialog(dono) != DialogResult.OK)
            return null;

        return new Conta
        {
            Nome = dialogo.nome.Text.Trim(),
            Usuario = dialogo.usuario.Text.Trim(),
            Senha = dialogo.senha.Text,
            Descricao = dialogo.descricao.Text.Trim()
        };
    }
}
