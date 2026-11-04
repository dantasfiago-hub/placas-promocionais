# Layout Oferta Correlato 10x21 cm

Modelo vertical de **100 × 210 mm**, convertido da placa de oferta enviada em DOCX.

## Campos dinâmicos

- `{descricao}`: descrição do produto.
- `{preco}`: preço da condição selecionada.
- `{inicio}` e `{validade}`: período da oferta importado do VR Master.

O aviso **“OU ENQUANTO DURAREM OS ESTOQUES”** permanece fixo no rodapé.

## Importar no Placas Promocionais

1. Baixe `oferta-correlato-10x21.json`.
2. Abra **Layouts salvos** e clique em **Importar JSON**.
3. Selecione o arquivo. O modelo ficará disponível para edição e para uso no lote.
4. Para mostrar a foto, abra a aba **Imagens**, clique em **Adicionar imagens…** e selecione a foto do produto.
5. Na aba **Produtos e importação**, selecione a condição da Spaten e use **Imagem do produto…** para vinculá-la.
6. Selecione o item no lote e escolha **Oferta Correlato 10x21 cm**.

O espaço de imagem do layout usa a origem dinâmica produto/família: a foto é obtida da biblioteca local do banco SQLite, não do arquivo JSON. Se o produto não tiver imagem vinculada, a área fica vazia na impressão.

## Observação

As datas e o preço são preenchidos pelos dados da condição selecionada. O texto do documento original não foi gravado como preço ou período fixo no modelo, para que ele possa ser reutilizado em outras ofertas.
