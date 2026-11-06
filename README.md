# Mago da Performance

Aplicativo Windows de utilidades para diagnóstico e otimização de desempenho:
identifica hardware e BIOS, ajusta registo e serviços, controla programas de
arranque, mede/liberta FPS, testa rede (download, upload, ping, DNS) e faz
limpeza — sempre com backup e restauro das alterações.

Escrito em **C# / WPF (.NET 10)**. O executável publicado é *self-contained*:
o PC do utilizador **não precisa de .NET nem de Python**.

## Baixar e usar

Duas formas, escolha uma:

**Instalador (recomendado)** — baixe `MagoSetup-<versão>.exe` na página de
[Releases](../../releases) e execute. Ele instala em
`%LOCALAPPDATA%\Programs\Mago da Performance`, cria atalho no Menu Iniciar,
opcionalmente na Área de Trabalho, registra em *Aplicativos e recursos* e traz
desinstalador próprio. Não precisa de administrador para instalar.

**Portátil** — baixe `MagoDaPerformance.exe` e coloque numa pasta qualquer.

Em ambos os casos o app pede elevação (UAC) sozinho; aceite para abrir com
permissões de administrador. Na primeira abertura ele coleta BIOS, hardware e a
lista de aplicativos instalados, então a janela pode levar alguns segundos para
ficar pronta.

## Páginas

| Página | O que faz |
|--------|-----------|
| **Painel** | Hardware, BIOS resumida, medidores de CPU/RAM/disco, FPS via PresentMon e manutenção rápida. |
| **Otimizar** | Perfis Equilibrado/Competitivo e módulos: GPU/HAGS, Windows Gaming, registo multimédia, energia, serviços, temp+DNS, rede, input lag, SSD. |
| **Rede** | Ping (router e internet), download/upload e benchmark de resolvedores DNS com troca por `netsh`. |
| **Aplicativos** | Lista Win32 e Store, pesquisa, desinstalação oficial e remoção forçada. |
| **Backup** | Captura, exportação/importação, restauro e pontos de restauro do Windows. |
| **BIOS** | Firmware, board, Secure Boot, TPM e atalho ao suporte do fabricante. |
| **Ferramentas** | Efeitos visuais, cache de shaders, arranque rápido, DNS, Explorer e programas de arranque. |

## Antes de otimizar

Use **Backup → Capturar agora**. O arquivo fica em
`%LOCALAPPDATA%\MagoDaPerformance\mago_backup.json` e é o que permite desfazer
ajustes de registo, serviços, BCD, energia e arranque.

## Onde ficam os dados

| Arquivo | Conteúdo |
|---------|----------|
| `MagoDaPerformance_log.txt` | Log de tudo que o app fez. |
| `mago_backup.json` | Backup dos valores alterados. |
| `mago_snapshots/` | Cópias datadas do backup. |
| `mago_settings.json` | Preferências (PresentMon, overlay). |

Tudo em `%LOCALAPPDATA%\MagoDaPerformance\`.

## Compilar a partir do código

Requisitos: .NET SDK 10 e, para o instalador, Inno Setup 6.

```bat
dotnet build MagoDaPerformance\MagoDaPerformance.csproj -c Release
dotnet run --project MagoDaPerformance\MagoDaPerformance.csproj
```

Para gerar o executável portátil (um único `.exe`, sem .NET no destino):

```bat
dotnet publish MagoDaPerformance\MagoDaPerformance.csproj -c Release -r win-x64 ^
  --self-contained true -p:PublishSingleFile=true ^
  -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -o publish
```

Para gerar também o instalador:

```bat
winget install JRSoftware.InnoSetup
powershell -ExecutionPolicy Bypass -File .\build-installer.ps1 -Version 0.2.0
```

Saída: `MagoSetup-0.2.0.exe` e o respetivo `.sha256` na raiz do repositório.
O script publica o app antes; use `-SkipExe` para reaproveitar `publish\`.

Diagnóstico sem abrir a interface:

```bat
publish\MagoDaPerformance.exe --selfcheck
```

## Conteúdo do repositório

| Caminho | Papel |
|---------|-------|
| `MagoDaPerformance/` | Projeto C#/WPF (Core, Services, Views). |
| `MagoSetup.iss` | Script do Inno Setup que gera o instalador. |
| `build-installer.ps1` | Publica o app e gera o instalador com um comando. |
| `icon.ico` | Ícone do executável e do instalador. |
| `icon.png` | Ícone usado na divulgação. |

## Avisos

- O app altera registo (`HKLM`), serviços, programas de arranque e BCD. **Exige
  administrador** e deve ser testado em máquina real antes de divulgar.
- Nem o `.exe` nem o instalador têm assinatura digital: o SmartScreen pode
  avisar e antivírus podem dar falso positivo. Baixe apenas desta página oficial.
- O executável self-contained é maior (~62 MB) porque leva o runtime .NET dentro.
