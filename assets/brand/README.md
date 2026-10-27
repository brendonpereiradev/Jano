# Marca do Jano

Jano é o nome de produto previsto para o Vistora a partir da versão 2.0.0, conforme o plano de reformulação visual. Por enquanto só a marca existe: o aplicativo continua exibindo o nome e o ícone do Vistora até a etapa de renomeação.

| Arquivo | Uso |
| --- | --- |
| `jano-mark.svg` | Símbolo quadrado (ícone, avatar). |
| `jano-logo-dark.svg` | Símbolo + nome sobre fundo escuro `#101318`. |
| `jano-logo-light.svg` | Símbolo + nome com fundo transparente, para superfícies claras. |
| `jano-logo-animated.svg` | Versão escura animada (~2,6 s, toca uma vez): a soleira e o arco se desenham, a pedra-chave aparece, cada pavimento, de baixo para cima, acende a metade azul e depois a menta, e o nome entra. Com `prefers-reduced-motion` mostra a logo estática. |

No aplicativo, o emblema vive em `src/Vistora.Desktop/Themes/Brand.xaml` em três desenhos: completo (48 px ou mais), pequeno (24 a 32 px) e mínimo (16 px). Os desenhos repetem o `jano-mark.svg`; ao mudar um, mude o outro.

## Conceito

Jano é o quarto da família, ao lado do Hefesto (a forja que monitora as impressoras), do Hermes (o mensageiro que as implanta) e do Talos (o guardião que vigia o servidor). É o deus romano das portas, dos começos e dos fins, com duas faces: uma olha para a entrada, outra para a saída. Em cada pavimento, o aplicativo abre um chamado e o fecha.

- **Portal em arco com soleira e pedra-chave**: a porta de Jano. Ocupa o lugar que a folha impressa tem no Hefesto e no Hermes.
- **Três pares de traços, um por pavimento**: a metade azul é a abertura do chamado e a metade menta, o fechamento. O vão entre elas é o eixo das duas faces.
- **Ladrilho grafite com borda azul**: a moldura comum à família.

## Cores e tipografia

| Papel | Cor |
| --- | --- |
| Ladrilho | `#1F2530` → `#101318` |
| Moldura | `#4C8DF6` a 40% |
| Portal e pedra-chave | `#E6E9EE` |
| Soleira | `#98A2B0` |
| Abertura | `#4C8DF6` |
| Fechamento | `#7FD1BE` |

O verde-menta `#7FD1BE` vem do monograma V do Vistora e fica só na marca (como a asa `#E8A05C` do Hermes), para não ser confundido com o verde de "concluído" `#3DD68C` da interface.

| Fonte | Onde |
| --- | --- |
| Bahnschrift SemiBold | Só o nome JANO, em caixa alta espaçada. |
| Consolas | O slogan "VISITAS PREVENTIVAS", em `#8DB5FA` no escuro e `#2F6FE0` no claro. |

Nos SVGs os textos estão como `<text>` com fontes de reserva (Barlow Semi Condensed, Cascadia Mono); para impressão, converta-os em contornos.
