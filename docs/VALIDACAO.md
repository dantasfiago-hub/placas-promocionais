# Validação da versão 0.1.0

Executada em 19/10/2026, ambiente Linux com .NET SDK 10.0.100 e Windows targeting.

- Build da solução: aprovado, zero erros e zero avisos.
- Testes: 16 aprovados, incluindo a suíte opcional com as três planilhas originais fornecidas.
- Arquivo real DATABASE.xlsx: 54.380 registros, 11.437 preços zero.
- Arquivo real de ofertas XLS: 14 registros.
- Arquivo real de promoções XLSX: 56 produtos em quatro grupos, sete itens com quantidade maior que um aguardando confirmação comercial.
- Todos os produtos das ofertas e promoções foram relacionados ao catálogo pela chave normalizada.
- Publicação autocontida para Windows x64 gerada.

Testes cobrem códigos, normalização de cabeçalhos, decimais brasileiros, promoção hierárquica, grupos inválidos, duplicações, preço zero, validade, conflito de condições, campos obrigatórios, snapshot de layout, paginação, persistência e backup.

Não executado neste ambiente: abertura da interface WPF, interação visual em Windows e teste físico de impressão. Esses itens permanecem no checklist de homologação. A compilação cruzada não os substitui.

O workflow Windows está preparado no código. A execução no GitHub depende de publicar o repositório; não há resultado de CI remoto nesta entrega local.
