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
| **Painel** | Resumo do PC (SO, CPU, GPU, RAM, discos) lido ao abrir, cartão de drivers com alerta de drivers antigos e atualização pelo Windows Update, FPS via PresentMon, **medição antes/depois** (FPS médio, 1% low e p95) e **diagnóstico rápido** (XMP/EXPO, limite térmico, saúde do disco, processos que comem CPU). |
| **Otimizar** | Perfis Equilibrado/Competitivo e módulos: GPU/HAGS, Windows Gaming, registo multimédia, energia, serviços, temp+DNS, rede, input lag, SSD. |
| **Rede** | Ao abrir faz pré-análise automática (adaptador, gateway, DNS atuais, link e ping). Botões para ping, download/upload, **latência sob carga (bufferbloat, nota A-F)** e benchmark de resolvedores DNS com troca por `netsh`. |
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

Tudo em `%LOCALAPPDATA%\MagoDaPerformance\`. O botão **Abrir log**, na barra lateral, abre o ficheiro de log.

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
powershell -ExecutionPolicy Bypass -File .\build-installer.ps1 -Version 0.2.3
```

Saída: `MagoSetup-0.2.3.exe` e o respetivo `.sha256` na raiz do repositório.
O script publica o app antes; use `-SkipExe` para reaproveitar `publish\`.

Diagnóstico sem abrir a interface:

```bat
publish\MagoDaPerformance.exe --selfcheck
```

## Atualizações

O app consulta a última release do GitHub **a cada arranque**, descarrega o
`MagoSetup-*.exe`, instala em silêncio e reabre sozinho. A verificação só
autualiza uma cópia instalada pelo setup (um `.exe` portátil não instala uma
segunda cópia). Em **Ferramentas → Atualizações** pode desligar a atualização
automática ou verificar na hora.

## Assinatura de código (Code signing policy)

A assinatura de código gratuita é fornecida pelo **SignPath.io**, com certificado
emitido pelo **SignPath Foundation**. A política completa e o passo a passo de
configuração estão em [`docs/CODE_SIGNING.md`](docs/CODE_SIGNING.md).

- **O que é assinado:** `MagoDaPerformance.exe` e o instalador `MagoSetup-*.exe`,
  gerados pelo workflow `.github/workflows/release.yml` a partir de tags `v*`.
- **Commits e revisões:** [histórico de commits](https://github.com/Nickallafe/Mago/commits/main)
  e [contribuidores](https://github.com/Nickallafe/Mago/graphs/contributors).
- **Aprovações:** [Nickallafe](https://github.com/Nickallafe) (proprietário do repositório).
- **Privacidade:** [política do SignPath](https://signpath.org/privacy-policy).
- **Verificar:** `Get-AuthenticodeSignature .\MagoSetup-0.2.7.exe` deve retornar
  `Status: Valid` com editor *SignPath Foundation*.

## Testes

Testes das funções puras (parsing de registo, nomes de apps da Store, CSV e
estatísticas do PresentMon, tokens do BCD, caminhos seguros, round-trip do backup,
preferências do overlay, hash da atualização e vereditos de RAM/CPU/disco/bufferbloat):

```bat
dotnet test tests\MagoDaPerformance.Tests\MagoDaPerformance.Tests.csproj
```

## Conteúdo do repositório

| Caminho | Papel |
|---------|-------|
| `MagoDaPerformance/` | Projeto C#/WPF (Core, Services, Views). |
| `MagoSetup.iss` | Script do Inno Setup que gera o instalador. |
| `build-installer.ps1` | Publica o app e gera o instalador com um comando. |
| `tests/` | Projeto xUnit com os testes das funções puras. |
| `MagoDaPerformance/Services/Updater.cs` | Auto-atualização pelo GitHub Releases. |
| `.github/workflows/release.yml` | Release assinada: testes, build, SignPath e publicação. |
| `signpath/` | Artifact configurations usadas pelo SignPath. |
| `docs/CODE_SIGNING.md` | Como obter e configurar a assinatura gratuita. |
| `LICENSE` | Licença MIT (exigência do SignPath Foundation). |
| `icon.ico` | Ícone do executável e do instalador. |
| `icon.png` | Ícone usado na divulgação. |

## Avisos

- O app altera registo (`HKLM`), serviços, programas de arranque e BCD. **Exige
  administrador** e deve ser testado em máquina real antes de divulgar.
- Nem o `.exe` nem o instalador têm assinatura digital: o SmartScreen pode
  avisar e antivírus podem dar falso positivo. Baixe apenas desta página oficial.
- O executável self-contained é maior (~62 MB) porque leva o runtime .NET dentro.
