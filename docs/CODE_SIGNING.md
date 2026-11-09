# Assinatura de código (SignPath Foundation)

O Mago da Performance é um projeto aberto e usa a **assinatura gratuita do
SignPath Foundation** para publicar executáveis assinados. Sem isso o Windows
mostra "Editor desconhecido" e o SmartScreen bloqueia a primeira execução.

## O que já está pronto no repositório

| Item | Papel |
|------|-------|
| `LICENSE` | Licença MIT (OSI-approved), exigida pelo SignPath. |
| `.github/workflows/release.yml` | Testa, compila, assina o `.exe` e o instalador e publica a release. |
| `signpath/app-exe.xml` | Artifact configuration do executável. |
| `signpath/installer-exe.xml` | Artifact configuration do instalador Inno Setup. |
| `README.md` → "Assinatura de código (Code signing policy)" | Política exigida pelo SignPath. |

## Passo 1 — Candidatura

1. Acesse <https://signpath.org/apply> e envie o projeto
   (`https://github.com/Nickallafe/Mago`), a licença MIT e uma descrição curta.
2. Informe que a build roda no GitHub Actions (`release.yml`).
3. Aguarde a revisão. Depois da aprovação você recebe acesso ao
   [SignPath.io](https://app.signpath.io) como organização de código aberto.

## Passo 2 — Configurar no SignPath

1. **Organization**: anote o *organization ID* (UUID mostrado nas configurações).
2. **Project**: crie com o slug `mago-da-performance`.
3. **Signing policy**: crie `release-signing` com a política
   **Open Source Code Signing** e origem verificada no GitHub
   (repositório `Nickallafe/Mago`, workflow `release.yml`, branch/tag `v*`).
4. **Artifact configurations** (cole o XML de `signpath/`):
   - `app-exe` → `signpath/app-exe.xml`
   - `installer-exe` → `signpath/installer-exe.xml`
5. Se a conta exigir **metadata restrictions**, imponha:
   - ProductName = `Mago da Performance`
   - CompanyName = `Ribanense Solucoes`
   - FileDescription e FileVersion preenchidos
6. **API token**: crie um token com permissão de criar *signing requests*.

## Passo 3 — Segredos no GitHub

Em **Settings → Secrets and variables → Actions**, crie:

| Secret | Valor |
|--------|-------|
| `SIGNPATH_API_TOKEN` | Token gerado no SignPath. |
| `SIGNPATH_ORG_ID` | Organization ID do SignPath. |

Sem esses dois segredos o workflow continua funcionando, mas **publica sem
assinatura** (os passos de assinatura são pulados).

## Passo 4 — Publicar

```bash
git tag v0.2.7
git push origin v0.2.7
```

O workflow `Release assinada (SignPath)` executa:

1. `dotnet test` (49 testes).
2. `dotnet publish` self-contained.
3. Upload do `.exe` → **assinatura** → download do `.exe` assinado.
4. `ISCC` gera o instalador com o `.exe` já assinado dentro.
5. Upload do instalador → **assinatura** → download do instalador assinado.
6. `Get-AuthenticodeSignature` valida o resultado.
7. SHA256 + `gh release create`.

Também é possível rodar manualmente em **Actions → Release assinada (SignPath) →
Run workflow**, informando a versão.

## Verificar a assinatura

```powershell
Get-AuthenticodeSignature .\MagoSetup-0.2.7.exe | Format-List Status, SignerCertificate
```

O esperado é `Status: Valid` e o editor **SignPath Foundation** (o certificado é
emitido para a fundação, que atesta que o binário saiu deste repositório).

Na interface do Windows: botão direito no `.exe` → **Propriedades** → aba
**Assinaturas digitais**.

## Limites conhecidos

- O desinstalador (`unins000.exe`) é gerado pelo Inno Setup em tempo de
  compilação e **não** é assinado por este fluxo; ele roda a partir do Painel de
  Controle, onde o aviso é menos frequente.
- A assinatura não remove o SmartScreen de imediato para editores novos; com o
  certificado do SignPath Foundation a reputação é compartilhada com os outros
  projetos abertos assinados por ele.
- O certificado é emitido para o SignPath Foundation, não para uma pessoa
  física. Isso é o esperado no programa gratuito.
- Se um dia o projeto for distribuído comercialmente, o SignPath Foundation não
  se aplica: será necessário um certificado OV próprio.
