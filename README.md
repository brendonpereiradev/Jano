<h1 align="center">Vistora</h1>

<p align="center">
  Automação de chamados de visitas preventivas no Jira, com execução pelo Microsoft Edge.
</p>

<p align="center">
  <img src="assets/screenshots/executar-visita.png" alt="Tela Executar visita com pavimentos e uma visita pendente" width="49%" />
  <img src="assets/screenshots/historico.png" alt="Histórico de visitas com resultados e etapas dos chamados" width="49%" />
</p>

---

## Sobre o projeto

Vistora é uma aplicação desktop em .NET 10 e WPF para registrar visitas preventivas por unidade e pavimento. Para cada pavimento selecionado, ela abre um chamado no Jira, atribui ao técnico conectado, inicia o atendimento e envia o fechamento, tudo pela interface do Jira em uma janela visível do Edge, sem usar a API.

A solução tem quatro projetos: `Vistora.Core` (regras e execução das visitas), `Vistora.Infrastructure` (persistência em JSON e diagnóstico), `Vistora.Automation.Edge` (operações no Jira) e `Vistora.Desktop` (interface). O progresso é salvo em `%LOCALAPPDATA%\Vistora` antes e depois de cada ação, o que permite retomar visitas interrompidas sem duplicar chamados.

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
   git clone https://github.com/brendonpereiradev/Vistora.git
   cd Vistora
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

4. Em **Configurações**, informe os endereços do Jira, do formulário e da fila. Em **Perfis e pavimentos**, cadastre a unidade e faça login no Edge quando solicitado.

### Comandos disponíveis

| Comando | Descrição |
|---|---|
| `.\scripts\Bootstrap.ps1` | Instala o SDK localmente em `.tools/dotnet`. |
| `.\scripts\Build.ps1` | Compila a solução em Release. |
| `.\scripts\Test.ps1` | Executa os testes; `-SkipBrowser` dispensa os testes que abrem o Edge e `-BrowserOnly` roda só esses. |
| `.\scripts\Publish.ps1` | Gera a aplicação com runtime incluído e o ZIP da versão em `output/release/`. |

---

## Documentação

- [Guia de uso](guides/USO.md): configuração de unidades, execução, retomada, diagnóstico e limpeza do histórico.
- [Manual de uso em PDF](guides/Manual-de-uso-Vistora.pdf): o mesmo conteúdo em formato para imprimir ou compartilhar, com sumário e capturas de tela.

---

## Licença

Distribuído sob a licença MIT. Consulte o arquivo [LICENSE](LICENSE).
