# Homologação Windows e impressora

Build automatizado não comprova comportamento do driver ou saída física. Executar no equipamento de destino:

1. Extrair o pacote completo em um Windows x64 sem SDK .NET. Abrir o executável.
2. Importar DATABASE.xlsx: esperar 54.380 produtos, com 11.437 preços zero.
3. Importar o XLS de ofertas: esperar 14 condições. Importar o XLSX de promoções: esperar 56 produtos em quatro grupos.
4. Reimportar cada arquivo: nenhum registro duplicado.
5. Pesquisar 010486 e 10486. Conferir as condições encontradas; preço normal da oferta é 7,55 e oferta é 5,89.
6. Selecionar catálogo e oferta do mesmo produto: adição deve solicitar escolha de apenas uma condição.
7. Marcar vários produtos, adicionar ao lote, pesquisar outro produto e adicioná-lo. Itens anteriores devem permanecer.
8. Selecionar produto com preço zero: geração deve bloquear.
9. Selecionar sorvete com quantidade 2: geração deve bloquear até confirmar a regra com a informação do VR Master. Não assumir que 10 é o total.
10. Confirmar regra e usar modelo com {condicao}. Conferir que quantidade e interpretação estão impressas.
11. Criar, salvar e duplicar um modelo. Fechar e abrir o programa: modelos devem permanecer.
12. Exportar JSON, importar em uma segunda instalação e conferir medidas/cores/campos.
13. Inserir texto longo: deve ajustar a fonte até o mínimo e bloquear quando ainda não couber.
14. Definir elemento fora da página, cor inválida ou campo inexistente: salvar deve bloquear.
15. Selecionar três produtos, definir cópias 1/2/3: a prévia deve conter seis placas.
16. Com o layout 95 × 135 mm, papel A4, margem 7 e intervalo 3: quatro placas por folha.
17. Gerar prévia, alterar quantidade ou layout e clicar em imprimir: o documento deve ser recriado com os valores atuais.
18. Escolher outra impressora/papel no diálogo: conferir a segunda prévia antes de enviar.
19. Imprimir uma folha em escala 100%. Medir a placa com régua; confirmar 95 × 135 mm, texto legível e ausência de corte.
20. Gerar PDF usando Microsoft Print to PDF e comparar com a prévia.
21. Cancelar no diálogo e na revisão: não deve enviar nada à fila.
22. Consultar histórico, mudar o modelo original e carregar o lote antigo: layout antigo deve ser preservado.
23. Tentar reimpressão com data posterior à validade: deve bloquear.
24. Gerar backup manual e testar restauração em uma cópia da instalação conforme README.
25. No editor de layouts, usar Ctrl+clique ou Shift+clique na placa e na lista de camadas para selecionar vários elementos; arrastar e usar Delete/duplicar deve operar sobre todos os selecionados.
26. Selecionar um elemento, alterar Rotação (graus), salvar o layout e conferir que o ângulo permanece após fechar e reabrir, inclusive na prévia de impressão.
27. Conferir as réguas em milímetros no topo e à esquerda da placa; seus valores devem acompanhar zoom, ajuste à tela e rolagem.
28. Conferir que os ícones de zoom e de ordenação de camadas aparecem e respondem ao clique.
29. Redimensionar textos e imagens arrastando as alças para aumentar e diminuir; a fonte deve acompanhar a caixa de texto, e as dimensões dos elementos não devem ser editáveis por campos numéricos.
30. Inserir cada forma, redimensionar, mover, mudar preenchimento/contorno, ordenar camadas, salvar e reabrir; verificar formas na prévia e no PDF.
31. Selecionar parte de um texto e aplicar família, tamanho, cor, negrito, itálico e sublinhado; repetir sem seleção para aplicar ao elemento inteiro, incluindo formatação de campos como {preco}.
32. Abrir o aplicativo com o tema MahApps.Metro e percorrer todas as abas, verificando a janela, controles, tabelas, diálogos, redimensionamento da janela e legibilidade em diferentes escalas de tela.
33. Na aba Produtos, pesquisar, filtrar, selecionar e adicionar itens ao lote; confirmar que a manutenção de dados pode ser expandida. Na aba Lote e impressão, revisar papel, data, margens, prévia e impressão em diferentes larguras da janela.
34. Conferir que somente a aba ativa fica destacada, que a coluna Normal (R$) e Cópias não são truncadas, e que o histórico usa dd/MM/aaaa HH:mm.
35. Alternar o tema claro/escuro e percorrer Produtos, Layouts, Lote, Histórico, Importações e Imagens; conferir contraste, foco, seleção e hover das linhas.
36. Com lote, prévia, importações e biblioteca vazios, conferir mensagens e ações de saída; adicionar dados e confirmar que os estados vazios desaparecem.
37. Clicar no contador do lote no rodapé e confirmar que abre Lote e impressão; conferir alinhamento à direita de números e cópias.
38. No editor de layouts, alternar entre Propriedades e Camadas; confirmar que Ajustar funciona ao abrir layouts e que Excluir fica separado das ações de salvar/exportar.
39. Abrir o aplicativo em Produtos e confirmar que a aba está ativa e que Cadastrar produto abre o cadastro. Redimensionar a janela entre 640 e 1280 px e conferir navegação, filtros, ações, editor, lote, prévia, histórico, importações e imagens; controles devem se reorganizar ou oferecer rolagem sem ficarem inacessíveis.

Registrar versão do Windows, modelo/driver da impressora, papel, resultado e eventuais ajustes necessários.
