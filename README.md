<p align="center">
  <img src="assets/brand/jano-logo-animated.svg" width="460" alt="Jano, visitas preventivas" />
</p>

<p align="center">
  Automação de chamados de visitas preventivas no Jira, com execução pelo Microsoft Edge.
</p>

---

## Sobre o projeto

O **Jano** (nome de produto do Vistora) é uma aplicação em .NET 10 e WPF que automatiza os chamados de visitas preventivas no Jira, por unidade e pavimento. Para cada pavimento selecionado, ela abre um chamado, atribui ao técnico conectado, inicia o atendimento e envia o fechamento, tudo pela interface do Jira em uma janela visível do Edge, sem usar a API.

A solução tem quatro projetos: `Vistora.Core` (regras e execução das visitas), `Vistora.Infrastructure` (persistência em JSON e diagnóstico), `Vistora.Automation.Edge` (operações no Jira) e `Vistora.Desktop` (interface). O progresso é salvo em `%LOCALAPPDATA%\Vistora` antes e depois de cada ação, o que permite retomar visitas interrompidas sem duplicar chamados.

---

## Rework visual

Na versão 2.0.0 o projeto recebeu um rework visual para se alinhar aos outros projetos do mesmo escopo, o Hefesto, o Hermes e o Talos. O aplicativo passou a se chamar **Jano**, o deus romano das portas, dos começos e dos fins, e ganhou um emblema próprio: um portal em arco em que cada pavimento tem a abertura do chamado em azul e o fechamento em menta. O tema escuro usa as superfícies do Talos, o tema claro as do Hefesto, e o emblema se anima na barra de título enquanto uma visita roda. O repositório também passou a se chamar Jano; os projetos `Vistora.*`, a pasta de dados e os scripts mantêm o nome antigo, e perfis, configurações e visitas pendentes da versão 1.x continuam valendo. Detalhes em [Marca do Jano](assets/brand/README.md).

---

## Funcionalidades

| Funcionalidade | Descrição |
|---|---|
| Perfis de unidades | Cadastra solicitante, unidade, pavimentos, resoluções e anexos opcionais. |
| Automação no Edge | Preenche os formulários e executa as transições do chamado pela interface do Jira. |
| Acompanhamento | Mostra o número do chamado e as etapas de cada pavimento durante a execução. |
| Retomada de pendências | Recupera os dados originais da visita e confere os chamados antes de continuar. Visitas sem chamados podem ser descartadas. |
| Conferência de resultados | Verifica responsável, status, resolução, comentário público e equipe no fechamento. |
| Histórico e diagnóstico | Reúne os resultados das visitas e exporta um ZIP de registros para análise. |
| Configuração pela tela | Endereços, equipe de fechamento, status, transições, rótulos dos campos do formulário e tempos de espera são editados em Configurações. |
| Tema claro e escuro | Troca na hora, pela barra de título ou pelas Configurações, e fica salvo no computador. |

---

## Stack

- **.NET 10 e WPF**: aplicativo Windows e interface.
- **Playwright para .NET**: automação do Jira no Microsoft Edge.
- **System.Text.Json**: perfis, configurações e progresso das visitas.
- **PowerShell**: preparação do SDK, compilação, testes e publicação.

---

## Como executar

### Pré-requisitos

- Windows x64 com Microsoft Edge.
- SDK .NET 10 (ou o script `Bootstrap.ps1`, que o instala na pasta do projeto).
- Conta no Jira com permissão para abrir, atribuir, iniciar e fechar os chamados do fluxo.

### Passo a passo

1. Clone o repositório:

   ```powershell
   git clone https://github.com/brendonpereiradev/Jano.git
   cd Jano
   ```

2. Prepare o SDK, se necessário, e compile:

   ```powershell
   .\scripts\Bootstrap.ps1
   .\scripts\Build.ps1
   ```

3. Inicie o aplicativo:

   ```powershell
   . .\scripts\Common.ps1
   & (Get-VistoraDotnet) run --project .\src\Vistora.Desktop -c Release --no-build
   ```

4. Em **Configurações**, informe os endereços do Jira, do formulário e da fila, a equipe de fechamento e os nomes do fluxo do Jira da sua equipe. Em **Perfis e pavimentos**, cadastre a unidade e faça login no Edge quando solicitado.

### Comandos disponíveis

| Comando | Descrição |
|---|---|
| `.\scripts\Bootstrap.ps1` | Instala o SDK localmente em `.tools/dotnet`. |
| `.\scripts\Build.ps1` | Compila a solução em Release. |
| `.\scripts\Test.ps1` | Executa os testes; `-SkipBrowser` dispensa os testes que abrem o Edge e `-BrowserOnly` roda só esses. |
| `.\scripts\Publish.ps1` | Gera a aplicação com runtime incluído e o ZIP da versão em `output/release/`. |
| `.\scripts\New-JanoIcons.ps1` | Gera o ícone do aplicativo a partir do emblema em `Themes/Brand.xaml`. |
| `python scripts\New-Documentation.py` | Gera a documentação em PDF a partir do Markdown (requer o ReportLab). |

---

## Documentação

- [Documentação (MD)](<guides/Documentação (MD).md>)
- [Documentação (PDF)](<guides/Documentação (PDF).pdf>)

---

## Licença

Distribuído sob a licença MIT. Consulte o arquivo [LICENSE](LICENSE).
