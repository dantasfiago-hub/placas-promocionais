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

Registrar versão do Windows, modelo/driver da impressora, papel, resultado e eventuais ajustes necessários.
