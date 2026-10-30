# Mago da Performance

Aplicativo Windows de utilidades para diagnóstico e otimização de desempenho:
identifica hardware e BIOS, ajusta registo e serviços, controla programas de
arranque, mede/liberta FPS e faz limpeza — sempre com backup e restauro das
alterações.

Interface em Tkinter, Python 3.8+ no código-fonte e **sem necessidade de Python**
quando usado o executável pronto da release.

## Baixar e usar

1. Abra a página de [Releases](../../releases) e baixe o `MagoDaPerformance-<versão>-win-x64.zip`.
2. Extraia numa pasta qualquer — não há instalador.
3. Rode **`MagoDaPerformance.exe`**. Ele pede elevação (UAC) sozinho; aceite para
   o app abrir com permissões de administrador.

Na primeira abertura ele coleta BIOS, hardware e a lista de aplicativos
instalados, então a janela pode levar alguns segundos para ficar pronta.

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

Requisitos: Python 3.8+ e PyInstaller.

```bat
python -m pip install --upgrade pyinstaller
pyinstaller mago.spec
```

O executável sai em `dist\MagoDaPerformance.exe` (onefile, sem console, com
Python e `psutil` embutidos).

Para rodar direto do fonte, sem empacotar:

```bat
python mago_da_performance.py
```

## Conteúdo do repositório

| Arquivo | Papel |
|---------|-------|
| `mago_da_performance.py` | Código do aplicativo. |
| `mago.spec` | Receita do PyInstaller para gerar o `.exe`. |
| `icon.ico` | Ícone do executável. |
| `icon.png` | Ícone usado na interface e na divulgação. |

## Avisos

- O app altera registo (`HKLM`), serviços, programas de arranque e BCD. **Exige
  administrador** e deve ser testado em máquina real antes de divulgar.
- O `.exe` é empacotado com PyInstaller e não tem assinatura digital: o
  SmartScreen pode avisar e antivírus podem dar falso positivo. Baixe apenas
  desta página oficial.
- O formato onefile extrai para `%TEMP%` a cada início (1–3 s a mais).
