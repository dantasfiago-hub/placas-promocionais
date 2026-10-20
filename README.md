# Placas Promocionais

Aplicativo desktop Windows, offline, para importar relatórios do VR Master, salvar layouts reutilizáveis e imprimir placas de vários produtos em lote.

## Usar no Windows

1. Extraia **todos** os arquivos do pacote Windows para uma pasta.
2. Execute `PlacasPromocionais.exe`. O pacote autocontido inclui o runtime .NET.
3. Em **Produtos e importação**, importe a base e os relatórios `.xls` / `.xlsx`.
4. Confira os totais e confirme a importação dos registros válidos. Linhas rejeitadas aparecem no relatório.
5. Pesquise os produtos, marque a primeira coluna e clique em **Adicionar marcados ao lote**. Você pode repetir a pesquisa e adicionar mais itens sem perder o lote.
6. Em **Layouts salvos**, ajuste textos, campos, posição, dimensões, cores e fontes. Clique em **Salvar layout**.
7. Em **Lote e impressão**, escolha o layout, a data de venda e o papel. Ajuste cópias por item.
8. Gere a prévia. Clique em **Imprimir / PDF**, selecione a impressora, confira a prévia final e confirme o envio.

Para PDF, selecione **Microsoft Print to PDF** no diálogo do Windows. A aplicação não necessita de Excel, VR Master instalado ou conexão com o banco do ERP.

## Layouts reutilizáveis

Um layout é independente dos produtos. Selecione 30 produtos, escolha um layout e gere 30 placas sem editar cada uma. Cópias são configuradas por produto.

O editor desta versão usa uma tabela de propriedades com prévia, não um canvas de arrastar e soltar. Cada elemento suporta texto fixo e campos:

| Campo | Conteúdo |
|---|---|
| `{descricao}` | Descrição do produto |
| `{codigo}` | Código como veio da origem |
| `{preco}` | Preço selecionado em reais |
| `{preco_normal}` | Preço normal do relatório de ofertas |
| `{validade}` | Data de término da condição |
| `{condicao}` | Texto explícito da promoção por quantidade |

Os layouts podem ser criados, duplicados, renomeados, editados, excluídos e exportados/importados em JSON. Os dados são salvos no SQLite local. A exportação JSON inclui toda a definição do layout, sem dados dos produtos.

O modelo inicial mede **95 × 135 mm**, permitindo quatro placas em A4 com margens. Quatro placas A6 completas (105 × 148 mm) não cabem em A4 com margens; o software não reduz a escala silenciosamente.

## Regras importantes

- Catálogo: `Produto`, `Descrição`, `Preço Venda`.
- Ofertas: `Código`, `Descrição`, `Preço Oferta`, `Preço Normal`, `Data Início`, `Data Término`.
- Promoções: linhas de grupo com código, período e quantidade, seguidas de produtos com `código - descrição` e `Valor Final`.
- A detecção usa cabeçalhos normalizados, não o nome do arquivo. Cabeçalhos podem mudar de ordem.
- Códigos `010486` e `10486` têm a mesma chave; a grafia original é preservada.
- Preços zero são importados para consulta, mas bloqueados na impressão.
- Para quantidade maior que 1, confirme no VR Master se o valor é unitário ou total do conjunto. Destaque a linha e clique em **Confirmar regra da promoção destacada**. A confirmação vale para aquele produto/condição; uma nova exportação substituindo a condição exige nova revisão.
- Não existe escolha automática do menor preço. O lote aceita uma condição por produto.
- O preço “de” vem de `Preço Normal`, nunca é inferido do catálogo.
- Reimportação do mesmo conteúdo é ignorada pelo hash SHA-256. Uma nova exportação atualiza condições de mesma identidade. Registros ausentes na nova exportação não são apagados automaticamente; não se deve interpretar ausência como cancelamento.
- A validade é checada na data de venda informada, inclusive em reimpressões.
- Histórico preserva os dados e o layout usados. Alterar um modelo não altera o histórico.
- “Enviado à fila” não comprova impressão física. Confirme o papel na impressora.
- Texto que não cabe na fonte mínima bloqueia a geração para evitar corte silencioso.

## Dados e backups

Dados locais em `%LOCALAPPDATA%\PlacasPromocionais\`:

- `placas.db`: produtos, condições, layouts e histórico.
- `backups\`: até quatro backups automáticos, realizados na inicialização quando passaram sete dias desde o último.
- `logs\`: até sete arquivos diários, limitados a aproximadamente 2 MB por arquivo.

Há também botão de backup manual. Para restaurar: feche **todas** as instâncias, faça uma cópia da pasta de dados atual, mova o banco e seus arquivos auxiliares `placas.db-wal` / `placas.db-shm` para essa cópia, coloque o backup no lugar de `placas.db` e reabra. Não copie um SQLite ativo com Explorer; use o botão de backup.

Não compartilhe `placas.db` por pasta de rede. Esta versão é de uso local. Sincronização multiestação exige uma API e banco central.

## Estrutura modular

```text
src/
  Placas.Domain/          Entidades, validações e campos dos layouts
  Placas.Application/     Casos de uso, contratos e cálculo de paginação
  Placas.Infrastructure/  Importadores Excel e persistência SQLite
  Placas.Rendering/       Composição WPF e documentos paginados
  Placas.Desktop/         Interface Windows e integração com impressoras
tests/Placas.Tests/       Regras, persistência e importação
```

As dependências apontam para Domain/Application. As telas usam serviços por interfaces; o domínio não conhece WPF, SQLite ou Excel. A interface utiliza bindings e modelos de linha observáveis, com coordenação de eventos no code-behind. Uma camada MVVM completa não é necessária para o núcleo desta versão e pode ser adicionada sem alterar as regras.

SQLite é acessado por `Microsoft.Data.Sqlite` com SQL parametrizado e transações. Preços são persistidos no JSON tipado das entidades e desserializados como `decimal`, sem colunas REAL. O esquema inicial usa `user_version=1`; futuras mudanças de esquema devem incluir migrações explícitas.

## Desenvolver

Requisitos: Windows 10/11 x64, .NET SDK 10, Visual Studio com desenvolvimento desktop .NET ou terminal.

```powershell
dotnet restore Placas.slnx
dotnet build Placas.slnx -c Release
dotnet test tests/Placas.Tests/Placas.Tests.csproj -c Release
dotnet run --project src/Placas.Desktop
```

Para gerar o pacote autocontido:

```powershell
.\scripts\build-windows.ps1
```

Para testar também as três planilhas originais, guardadas fora do repositório:

```powershell
$env:VR_FIXTURES_DIR = 'C:\Dados\ExportacoesVR'
dotnet test tests/Placas.Tests/Placas.Tests.csproj -c Release
```

O teste opcional espera os nomes originais dos arquivos de 15/10/2026. Em CI, apenas fixtures sintéticas são usadas. As planilhas comerciais, custos, banco local e relatórios do usuário **não estão incluídos** no código-fonte.

## GitHub

O workflow `.github/workflows/build.yml` compila no Windows, executa testes e disponibiliza o executável como artefato `PlacasPromocionais-windows-x64`.

Para criar um repositório privado na sua conta com GitHub CLI:

```powershell
winget install GitHub.cli
gh auth login
.\scripts\publicar-github.ps1
```

O script não substitui um repositório existente. Se preferir, crie um repositório vazio chamado `placas-promocionais` no GitHub e publique o projeto pelo Git.

## Limites desta versão

- Interface funcional baseada nos conceitos apresentados; não é uma reprodução pixel a pixel dos mockups.
- Editor por propriedades: sem arrastar e soltar, imagens/logotipos, código de barras ou editor de vetores nesta versão.
- PDF pelo driver do Windows; não há exportador PDF independente.
- Sem autenticação, nuvem, integração direta com ERP ou operação concorrente em vários computadores.
- Código e pacote podem ser compilados em Linux com Windows targeting; abrir a interface e testar impressoras exige Windows.

Consulte [docs/TESTES-MANUAIS.md](docs/TESTES-MANUAIS.md) para a homologação na loja.
