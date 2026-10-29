# Dimensionador Tipo Grelha 0.36

## Organização das pastas

```
DIMENSIONADOR TIPO GRELHA/
├─ PROGRAMA/                    programa (código, catálogo de bombas, instalador)
│  ├─ Grelha.sln                solução com os três projetos
│  ├─ Grelha.IfcInspector/      leitor do IFC, cálculo hidráulico, seleção de bombas e memorial
│  ├─ Grelha.IfcInspector.UI/   janela do programa
│  ├─ Shared/                   código comum (gráfico da bomba)
│  ├─ Catalogo/                 bombas_incendio.json/.csv (lidos pelo programa)
│  ├─ Ferramentas/              extract_catalog.py (extração do catálogo PDF)
│  ├─ Executar_Grelha.bat       compila e abre a janela
│  ├─ Instalar_Grelha.bat/.ps1  publica e instala no computador
│  └─ dist/                     pacotes publicados por versão
├─ IFC/                         modelos IFC, memorial PDF e saídas da análise
├─ CATALOGO/                    catálogo do fabricante (PDF)
├─ NORMAS/                      NBR 10897 e NBR 16704
├─ PROJETO MODELO/              pranchas do projeto de referência
├─ logs/                        registros de execução
└─ RELATORIOS/                  relatórios de atualização por versão (internos)
```

Aplicativo para abrir um IFC4 e exportar o inventário da rede de sprinklers, uma auditoria geométrica e ligações candidatas.

Execute `PROGRAMA/Executar_Grelha.bat` com duplo clique para abrir a janela do programa. Selecione o IFC e clique em **Analisar IFC**. Também é possível arrastar um arquivo `.ifc` sobre o `.bat`.

Quando houver um único PDF com `MEM` e `SPK` no nome ao lado do IFC, o leitor extrai os critérios, a tabela de sprinklers, os dados da bomba e a reserva. Valida o K√P, pressão mínima, quantidade de bicos e vazão resumida, e associa os bicos ao IFC por alinhamento das coordenadas. Valores hidráulicos permanecem identificados como referência do memorial, não como resultado calculado pelo aplicativo.

### Planilhas de revisão da topologia

O cálculo hidráulico **não** depende de ligações confirmadas: a rede é montada automaticamente pela geometria (ver "Dimensionamento hidráulico"). As planilhas abaixo servem para conferir essa montagem e para excluir ligações erradas:

- `*.operation-area-candidates.csv`: cada bico da área e as extremidades de tubo até 5 cm.
- `*.operation-area-topology-review.csv` e `*.operation-area-branch-review.csv`: relações candidatas nos ramais da área, com as colunas `Decision` (`Pendente`, `Confirmada`, `Rejeitada`), `ReviewNotes` e `CurrentCandidate`. As decisões são preservadas ao reprocessar o IFC.
- `*.operation-area-confirmed-topology.csv` e `*.operation-area-topology-audit.csv`: ligações marcadas `Confirmada` e a conectividade que elas formam.

Efeito no cálculo: ligações `Rejeitada` são retiradas da rede montada; `Confirmada` e `Pendente` não mudam nada (a ligação geométrica já é usada). Por isso o resultado só vale depois de conferir a rede montada numa área conhecida (planilha `*.hydraulic-segments.csv` e o 3D).

Para executar pelo terminal:

```powershell
dotnet run -- "C:\caminho\modelo.ifc"
```

O inventário JSON e os arquivos CSV são gravados ao lado do IFC, sem alterar o modelo. A análise filtra `Identificação_Elemento.Rede = Sprinklers` para não misturar a rede de hidrantes. O inventário inclui a posição global dos tubos, conexões e terminais; `*.connectivity-audit.csv` registra as coordenadas globais das extremidades e os vizinhos em até 20 cm; `*.network-candidates.csv` lista os pares candidatos; `*.network-components.csv` atribui cada tubo a grupos por contato direto e por proximidade ampliada. Esses grupos são geométricos, não conexões confirmadas. Confira o resultado numa área conhecida antes de formar a rede hidráulica. 

## Dimensionamento hidráulico (0.19)

Com o memorial ao lado do IFC, o leitor calcula a área de operação por Hazen-Williams (gradiente global): bicos como emissores Q = K√P, diâmetro interno do IFC, comprimento pela geometria, C = 120 (aço) ou 150 (cobre) e comprimentos equivalentes da NFPA 13 para cotovelos e saídas laterais de tês. A pressão mínima é a maior entre a do memorial e a exigida pela densidade.

A topologia é montada automaticamente (contato ≤ 1 mm; conexões e bicos ≤ 5 cm). Ligações marcadas `Rejeitada` nas planilhas de revisão são excluídas. A alimentação é a extremidade livre de maior diâmetro e menor cota; para fixar outra, use `--alimentacao <GlobalId do tubo>`.

Saídas: `*.hydraulic-sprinklers.csv` (pressão e vazão por bico, comparadas ao memorial), `*.hydraulic-pipes.csv` (vazão, velocidade e perda por tubo) e a seção `HydraulicCalculation` do inventário. Botões **Dimensionamento: bicos** e **Dimensionamento: tubos** na interface.


## Bomba, recalque e sucção (0.20)

Válvulas (`IfcValve`) e bombas (`IfcPump`) da rede Sprinklers entram no cálculo. Uma válvula liga o par de pontas soltas alinhadas que passa a até 5 cm da sua origem. A bomba liga as duas pontas soltas mais próximas, até 1 m: o bocal de maior diâmetro é a sucção. Quando há mais de uma bomba ligada à área, usa-se a de maior recalque e as demais ficam desligadas (reserva e jockey). As tomadas d'água do reservatório são carga fixa no nível da tomada, mais `--nivel-reservatorio <m>` (campo **Nível do reservatório** na interface).

Comprimentos equivalentes: valores da tabela "Conexões / L equivalente" do memorial, por item e bitola (no tê, o menor valor é a passagem direta e o maior a saída lateral). Itens ausentes usam a tabela usual de aço.

Curva da bomba: `*.pump-curve.csv` ao lado do IFC, com colunas `Flow_l_s,Head_m` e pelo menos 2 pontos (botão **Curva da bomba**). Com a curva preenchida, o programa calcula o ponto de operação e avisa se algum bico fica abaixo da pressão mínima. Sem a curva, calcula a altura manométrica necessária para atender a pressão mínima.

O tubo de alimentação (campo na interface ou `--alimentacao`) substitui a bomba e o reservatório quando informado.


## Seleção da bomba e memorial de cálculo (0.21)

O catálogo `PROGRAMA/Catalogo/bombas_incendio.json` (extraído do PDF da Schneider em `CATALOGO/` por `PROGRAMA/Ferramentas/extract_catalog.py`) acompanha o leitor. Em cada análise, cada bomba principal do catálogo é colocada no lugar da bomba do IFC e o programa calcula o ponto de operação na rede real. A lista `*.pump-selection.csv` traz as verificações da ABNT NBR 16704:2019, usando o ponto de operação como ponto nominal:

- pressão mínima em todos os chuveiros da área;
- ponto dentro da faixa tabelada no catálogo;
- pressão nominal ≥ 2,7 bar (4.8.2);
- 150% da vazão com ≥ 65% da pressão (6.2.1), sinalizado quando extrapolado da curva;
- pressão sem vazão ≤ 140% (6.2.2);
- sucção ≥ −0,2 bar (4.14.3.1).

Ordem da lista: atende, linha BPI, menor potência. Na interface, **Selecionar bomba** abre a lista; **Gerar memorial** recalcula com a bomba escolhida e grava `Memorial_Calculo_<IFC>_<data>.docx` ao lado do IFC (pelo terminal, `--memorial <Id>`). O memorial inclui critérios, chuveiros, planilha por trecho, bomba e verificações, jóquei sugerida (4.22.3) e reserva (NBR 10897, Tabela 27).

Regras da NBR 10897 aplicadas ao cálculo: perda do tê só na mudança de direção (9.4.4.7), C da Tabela 30 e pressão mínima de 48 kPa (9.4.4.10).


## Critério de perda em tês e válvulas (0.22)

Perda do tê conforme NBR 10897, 9.4.4.7: só na mudança de direção, na saída lateral. A opção de cobrar a perda também na passagem direta (critério dos memoriais do QiBuilder) foi retirada na 0.25, porque, com as conexões deslocadas contadas, ela somava perdas em duplicidade.

Válvulas cuja indicação traz só um código (VA, RG, VR) usam a bitola do tubo ligado para buscar o comprimento equivalente no memorial. A válvula de segurança em linha é tratada como retenção (no projeto modelo, "com válvula de retenção integrada").


## Conexões deslocadas (0.23)

Nas bitolas grandes, a origem da conexão (centro da peça) fica a mais de 5 cm da ponta do tubo, e muitas vezes os tubos se tocam no vértice sem a peça. Depois da ligação normal, as conexões com portas faltando recebem pontas soltas até 2,5 × a maior bitola da peça. As conexões que continuam sem tubo são atribuídas ao nó de tubos compatível mais próximo que ainda não tenha conexão, só para somar a perda localizada. A quantidade aparece nos avisos do cálculo. No projeto modelo, 254 conexões passaram a contar, a maioria cotovelos e tês de 3" e 4" do barrilete e da sucção.

A partir da 0.24, a atribuição exige geometria compatível no nó: tê com 3 tubos; cotovelo de 90° com 2 tubos em ângulo reto (45° com desvio de 45°); redução com 2 tubos alinhados de diâmetros diferentes.


## Tela de seleção da bomba (0.25)

Estilo das janelas de bomba do QiBuilder: escolha de **Grupo** (bocais) e **Peça** (modelo, potência, rotor), gráfico *curva de vazão & altura* com a curva da bomba (catálogo; tracejada fora da faixa tabelada), a curva do sistema da rede do IFC (`*.system-curve.csv`), o ponto de funcionamento, o ponto mínimo da rede e o ponto de 150%. Abaixo, as propriedades: rede (vazão e altura mínimas), bomba (funcionamento, pressões, potência do motor e hidráulica, rotor, bocais, pressão sem vazão, sucção) e verificações da NBR 16704. NPSH e rendimento aparecem como não informados, porque o catálogo não traz esses dados. **Gerar memorial** usa a peça escolhida.

A partir da 0.26, o memorial de cálculo traz o mesmo gráfico (curva da bomba sobre a curva do sistema, ponto de operação, ponto mínimo da rede e 150% da vazão), conforme a NBR 10897, 9.3.4. O desenho fica em `Shared/PumpChart.cs`, usado pela tela e pelo leitor.

## Janela com visualizador 3D (0.27)

A janela principal segue o visual do Visor IFC (barra superior, painel lateral com abas Projeto/Bicos/Bombas, propriedades e área 3D). O 3D mostra a rede de sprinklers do IFC com os diâmetros reais (ampliados 2,5× para leitura; opção Diâmetro real), bicos, conexões, válvulas e bombas, coloridos pela situação do cálculo, vazão, velocidade ou diâmetro; os bicos da área trazem número e pressão. Clique seleciona e mostra as propriedades; duplo clique aproxima. A interface fica em `Grelha.IfcInspector.UI/Web` (three.js 0.170, licença MIT em `Web/lib`) e roda numa WebView2; os dados vêm de `*.network-3d.json`, gerado pelo leitor. Sem o WebView2 Runtime, abre a janela clássica.


## Elementos do IFC no 3D, planta 2D e área escolhida (0.28)

O leitor grava `*.network.ifc` só com a rede de sprinklers (tubos, conexões, bicos, válvulas e bombas), refeito quando o IFC muda; o visualizador lê esse arquivo com web-ifc 0.0.66 (MPL-2.0, em `Web/lib`) e desenha os elementos com a geometria original, coloridos pelo cálculo. Sem o arquivo, volta às formas simplificadas.

Vista **2D**: planta por pavimento (cota dos bicos). **Selecionar área**: arraste um retângulo sobre os bicos (Shift soma, Ctrl retira, clique alterna um bico). Na aba Projeto, em Área de operação, escolha **Escolhida no 2D**, informe densidade, área de aplicação, K, pressão mínima e risco, e clique em **Calcular com a área escolhida**. A área é gravada em `*.operation-area.json` e passada ao leitor com `--area`; as análises e memoriais seguintes usam essa área até voltar para **Do memorial**.

Na aba Bombas, clicar numa bomba abre a janela **Bomba** (curva da bomba e do sistema, ponto de funcionamento e dados) já nessa bomba.


## Área mais desfavorável, forma da área e cache (0.29)

- **Sugerir a área mais desfavorável** (aba Projeto, área Escolhida no 2D) ou `--area-auto <critérios.json>`: em cada pavimento (IfcBuildingStorey), o leitor identifica a direção dos ramais e os espaçamentos, monta retângulos conforme a NBR 10897 (lado paralelo aos ramais ≥ 1,2 √A; bicos = A ÷ área por bico; área contígua até 1,3 × A) a partir dos 16 bicos mais distantes das prumadas nas quatro orientações, inclui a área do memorial quando houver, calcula todas na mesma rede com a pressão mínima de cada uma e usa a que exige maior altura manométrica. Resultado em `*.area-candidates.json` e `*.operation-area.json`.
- A área escolhida à mão é conferida na hora: um só pavimento, número de bicos, lado paralelo aos ramais e área contígua.
- A vista 2D separa os pavimentos pelo IfcBuildingStorey.
- A geometria do IFC da rede fica em cache no WebView2 (IndexedDB); o 3D reabre em cerca de 3 s em vez de 7 s.


## Modelo reto e busca mais rápida (0.30)

A vista gira a rede para que os ramais do pavimento principal fiquem na horizontal (o modelo do projeto está girado cerca de 5°). A área calculada (escolhida ou sugerida) aparece selecionada na planta ao reabrir os resultados. Na busca da área mais desfavorável, cada área parte do resultado da anterior: 42 áreas em cerca de 27 s (antes cerca de 75 s).

## Navegação igual à do Visor IFC (0.31)

O 3D usa a biblioteca camera-controls 2.9.0 (MIT, em `Web/lib`) com a mesma configuração do Visor IFC: esquerdo orbita, roda pressionada move, roda aproxima na direção do cursor sem travar perto dos elementos (distância mínima 0,3 m), movimento suavizado e clique num elemento passa a ser o centro da órbita. O botão direito abre o menu: Aproximar, Isolar, Isolar o ramal, Ocultar, Mostrar todos e Enquadrar. Na planta 2D o esquerdo move e a roda aproxima no cursor. Esc fecha o menu e sai da seleção de área.

## Desenho do 3D como no Visor IFC (0.32)

- A rede inteira é desenhada numa só malha em lote (BatchedMesh do three.js), com a geometria original do IFC.
  Antes eram cerca de 3.600 malhas separadas; agora é uma chamada de desenho. Cor, ocultar e isolar só
  mudam o estado de cada item, sem recriar geometria (mesmo princípio do Visor IFC).
- Teclas do Visor IFC no 3D: H oculta a seleção · I isola · M mostra tudo · F enquadra · Esc limpa a seleção.
  Duplo clique na roda enquadra tudo.

## Correções da revisão (0.33)

- O IFC da rede (`*.network.ifc`) agora também é refeito quando o leitor é mais novo que ele, e não só quando o IFC muda.
- O texto sobre topologia foi corrigido: o cálculo usa a rede montada pela geometria; só as ligações `Rejeitada` alteram o cálculo.

## Revisão de engenharia (0.34)

- **Bomba a confirmar:** quando o ponto de 150% da vazão fica além da curva do catálogo, a altura é extrapolada. A bomba deixa de
  "atender" e passa a "A confirmar", com a curva de ensaio do fabricante (NBR 16704, 4.5). Na tela aparece com "?".
- **Montagem da rede:** no 3D, "Colorir → Montagem da rede" mostra o que o cálculo ligou por contato (cinza), o que ligou por
  proximidade (laranja), ligações incompletas (vermelho) e pontas de tubo sem nada ligado (bolinhas vermelhas; as pontas em bicos
  fora da área não contam). As propriedades de cada elemento dizem como ele foi ligado e a distância. Confira antes de aceitar o resultado.
- **Solucionador esparso:** o sistema de cada iteração é resolvido por gradiente conjugado com matriz esparsa; acabou o limite de
  4.000 nós da matriz densa. No IFC modelo o resultado é o mesmo da versão anterior (17,27 L/s; 53,06 m).

## Convergência garantida do solucionador (0.36)

- O gradiente conjugado confere o resíduo verdadeiro (b − A·x) no fim; acima de 1e-9 (relativo) o cálculo para com erro,
  em vez de aceitar uma solução incompleta.
- Resíduos não finitos e esgotamento forçado do limite de iterações são rejeitados pelo autoteste.
- Autoteste: `Grelha.IfcInspector.exe --teste-solucionador` monta grelhas de 100 a 90.000 nós com resistências variadas,
  mede o tempo e compara com o Cholesky denso até 2.500 nós.
- As extremidades de tubo sem conexão geométrica no componente que alimenta a área passam a aparecer nos avisos do cálculo e do memorial; valide-as em
  "Montagem da rede" para distinguir fim de linha intencional de conexão ausente.
- A execução de referência atual foi comparada ao memorial PDF existente: a pressão mínima coincidiu (6,34 m.c.a.),
  mas a vazão calculada (17,27 L/s) difere da vazão de referência (16,63 L/s) e a altura requerida (53,06 m) difere
  da altura publicada (57,28 m). Portanto, o cálculo ainda exige revisão da topologia e dos critérios antes de uso de projeto.
