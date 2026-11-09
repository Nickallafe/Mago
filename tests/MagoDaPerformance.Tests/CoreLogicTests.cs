using System.IO;
using MagoDaPerformance;
using Microsoft.Win32;

namespace MagoTests;

public class RegParseTests
{
    [Theory]
    [InlineData(@"HKLM\SOFTWARE\Teste", "SOFTWARE\\Teste")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Teste", "SOFTWARE\\Teste")]
    [InlineData(@"HKCU\Software\Teste\Sub", "Software\\Teste\\Sub")]
    [InlineData(@"HKEY_CURRENT_USER\Software", "Software")]
    [InlineData(@"HKCR\Applications", "Applications")]
    public void Parse_devolve_hive_e_subchave(string path, string expectedSubKey)
    {
        var (_, sub) = Reg.Parse(path);
        Assert.Equal(expectedSubKey, sub);
    }

    [Theory]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\X")]
    [InlineData(@"HKCU\Software\X")]
    public void Parse_distingue_hives(string path)
    {
        var (hive, _) = Reg.Parse(path);
        Assert.True(hive is RegistryHive.LocalMachine or RegistryHive.CurrentUser);
    }

    [Theory]
    [InlineData(@"C:\Windows")]
    [InlineData(@"")]
    [InlineData(@"Software\SemHive")]
    public void Parse_rejeita_caminhos_invalidos(string path) =>
        Assert.Throws<ArgumentException>(() => Reg.Parse(path));
}

public class StorePackageNameTests
{
    [Theory]
    // ResourceId vazio (dois underscores): o caso da maioria das apps da Store.
    [InlineData("Microsoft.WindowsCalculator_11.2210.0.0_x64__8wekyb3d8bbwe",
        "Microsoft.WindowsCalculator_8wekyb3d8bbwe")]
    [InlineData("Microsoft.WindowsStore_22303.1401.2.0_x64__8wekyb3d8bbwe",
        "Microsoft.WindowsStore_8wekyb3d8bbwe")]
    [InlineData("Microsoft.Photos_2024.1.0.0_neutral_~_8wekyb3d8bbwe",
        "Microsoft.Photos_8wekyb3d8bbwe")]
    [InlineData("", "")]
    public void FamilyName_remove_versao_e_arquitetura(string fullName, string expected) =>
        Assert.Equal(expected, Software.FamilyName(fullName));
}

public class UninstallPathTests
{
    [Theory]
    [InlineData(@"Microsoft.PowerShell.Core\Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Vendor\App",
        @"HKLM\SOFTWARE\Vendor\App")]
    [InlineData(@"Registry::HKEY_CURRENT_USER\Software\App", @"HKCU\Software\App")]
    [InlineData("", "")]
    public void PspathToReg_remove_prefixo_do_powershell(string pspath, string expected) =>
        Assert.Equal(expected, Software.PspathToReg(pspath));

    [Theory]
    [InlineData("")]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\System32\drivers")]
    public void IsSafeDeletePath_bloqueia_caminhos_de_sistema(string path) =>
        Assert.False(Software.IsSafeDeletePath(path));

    [Fact]
    public void IsSafeDeletePath_aceita_pasta_de_programa()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "MagoTestePasta");
        Assert.True(Software.IsSafeDeletePath(path));
    }
}

public class PresentMonCsvTests
{
    [Fact]
    public void SplitCsv_cabecalho_tem_tres_colunas()
    {
        var cells = FpsMonitor.SplitCsv("Application,ProcessID,MsBetweenPresents");
        Assert.Equal(3, cells.Count);
        Assert.Equal("MsBetweenPresents", cells[2]);
    }

    [Fact]
    public void SplitCsv_respeita_nome_com_virgula()
    {
        var cells = FpsMonitor.SplitCsv("\"Game, The\",42,16.67");
        Assert.Equal(3, cells.Count);
        Assert.Equal("Game, The", cells[0]);
        Assert.Equal("42", cells[1]);
    }

    [Fact]
    public void SplitCsv_desescapa_aspas_duplicadas()
    {
        var cells = FpsMonitor.SplitCsv("\"He said \"\"hi\"\"\",1");
        Assert.Equal(2, cells.Count);
        Assert.Equal("He said \"hi\"", cells[0]);
    }
}

public class BcdTokenTests
{
    [Theory]
    [InlineData("Yes", "yes")]
    [InlineData("Sim", "yes")]
    [InlineData("true", "yes")]
    [InlineData("No", "no")]
    [InlineData("Não", "no")]
    [InlineData("Nao", "no")]
    [InlineData(" false ", "no")]
    [InlineData("0x1", "0x1")]
    public void NormalizeBcdToken_uniformiza_booleanos(string input, string expected) =>
        Assert.Equal(expected, TweakEngine.NormalizeBcdToken(input));

    [Fact]
    public void NormalizeBcdToken_mantem_ausente_como_nulo() =>
        Assert.Null(TweakEngine.NormalizeBcdToken(null));

    [Theory]
    [InlineData("Receive Window Auto-Tuning Level")]
    [InlineData("ECN Capability")]
    [InlineData("RFC 1323 Timestamps")]
    public void TcpParamNames_mapeia_parametros_que_o_mago_altera(string display) =>
        Assert.True(TweakEngine.TcpParamNames.ContainsKey(display));
}

public class BackupRoundTripTests
{
    [Fact]
    public void Guarda_e_le_estado_original_dos_tweaks()
    {
        var path = Path.Combine(Path.GetTempPath(), "mago_test_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new BackupStore(path);
            store.Data.Touched.TcpBefore["ECN Capability"] = "enabled";
            store.Data.Touched.DefragWasEnabled = false;
            store.Data.Touched.MemoryCompressionWasEnabled = true;
            store.Data.Touched.HibernateWasEnabled = true;
            store.Data.Touched.TrimBefore = 1;
            store.RememberPower("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
            store.Save();

            var reloaded = new BackupStore(path);
            Assert.Equal("enabled", reloaded.Data.Touched.TcpBefore["ECN Capability"]);
            Assert.False(reloaded.Data.Touched.DefragWasEnabled);
            Assert.True(reloaded.Data.Touched.MemoryCompressionWasEnabled);
            Assert.True(reloaded.Data.Touched.HibernateWasEnabled);
            Assert.Equal(1, reloaded.Data.Touched.TrimBefore);
            Assert.True(reloaded.HasBackup);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}

public class ReportRedactionTests
{
    [Fact]
    public void Redact_esconde_nome_de_utilizador()
    {
        var userName = Environment.UserName;
        var line = $@"Snapshot de registo falhou em C:\Users\{userName}\AppData\Local\Mago";
        var redacted = Report.Redact(line);
        Assert.Contains("<perfil>", redacted);
        Assert.DoesNotContain(userName, redacted, StringComparison.OrdinalIgnoreCase);
    }
}
