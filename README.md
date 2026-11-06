# Mago da Performance

Aplicativo Windows de utilidades para diagnóstico e otimização de desempenho:
identifica hardware e BIOS, ajusta registo e serviços, controla programas de
arranque, mede/liberta FPS, testa rede (download, upload, ping, DNS) e faz
limpeza — sempre com backup e restauro das alterações.

Interface em Tkinter, Python 3.8+ no código-fonte e **sem necessidade de Python**
quando usado o executável ou o instalador pronto da release.

## Baixar e usar

Duas formas, escolha uma:

**Instalador (recomendado)** — baixe `MagoSetup-<versão>.exe` na página de
[Releases](../../releases) e execute. Ele instala em
`%LOCALAPPDATA%\Programs\Mago da Performance`, cria atalho no Menu Iniciar,
opcionalmente na Área de Trabalho, registra em *Aplicativos e recursos* e traz
desinstalador próprio. Não precisa de administrador para instalar.

**Portátil** — baixe `MagoDaPerformance-<versão>-win-x64.zip`, extraia numa pasta
qualquer e rode **`MagoDaPerformance.exe`**.

Em ambos os casos o app pede elevação (UAC) sozinho; aceite para abrir com
permissões de administrador. Na primeira abertura ele coleta BIOS, hardware e a
lista de aplicativos instalados, então a janela pode levar alguns segundos para
ficar pronta.

## Antes de otimizar

Use **Backup e restauro → criar backup**. O arquivo fica em
`%LOCALAPPDATA%\MagoDaPerformance\mago_backup.json` e é o que permite desfazer
ajustes de registo, serviços e arranque.

## Onde ficam os dados

| Arquivo | Conteúdo |
|---------|----------|
| `MagoDaPerformance_log.txt` | Log de tudo que o app fez. |
| `mago_backup.json` | Backup dos valores alterados. |
| `mago_snapshots/` | Cópias datadas do backup. |

Tudo em `%LOCALAPPDATA%\MagoDaPerformance\`.

## Compilar a partir do código

Requisitos: Python 3.8+ e PyInstaller (e Inno Setup 6 para o instalador).

```bat
python -m pip install --upgrade pyinstaller
pyinstaller mago.spec
```

O executável sai em `dist\MagoDaPerformance.exe` (onefile, sem console, com
Python e `psutil` embutidos).

Para gerar também o instalador:

```bat
winget install JRSoftware.InnoSetup
powershell -ExecutionPolicy Bypass -File .\build-installer.ps1 -Version 0.1.2
```

Saída: `MagoSetup-0.1.2.exe` e o respetivo `.sha256` na raiz do repositório.
O script roda o PyInstaller antes; use `-SkipExe` para reaproveitar o
`dist\MagoDaPerformance.exe` já existente.

Para rodar direto do fonte, sem empacotar:

```bat
python mago_da_performance.py
```

## Conteúdo do repositório

| Arquivo | Papel |
|---------|-------|
| `mago_da_performance.py` | Código do aplicativo. |
| `mago.spec` | Receita do PyInstaller para gerar o `.exe`. |
| `MagoSetup.iss` | Script do Inno Setup que gera o instalador. |
| `build-installer.ps1` | Gera o `.exe` e o instalador com um comando. |
| `icon.ico` | Ícone do executável e do instalador. |
| `icon.png` | Ícone usado na interface e na divulgação. |

## Avisos

- O app altera registo (`HKLM`), serviços, programas de arranque e BCD. **Exige
  administrador** e deve ser testado em máquina real antes de divulgar.
- Nem o `.exe` nem o instalador têm assinatura digital: o SmartScreen pode
  avisar e antivírus podem dar falso positivo. Baixe apenas desta página oficial.
- O formato onefile extrai para `%TEMP%` a cada início (1–3 s a mais).
