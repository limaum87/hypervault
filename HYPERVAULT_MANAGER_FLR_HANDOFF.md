# Handoff — HyperVaultManager: File-Level Restore (FLR)

## Escopo da próxima fase

Implementar somente a integração e a interface do `HyperVaultManager` para File-Level Restore de backups de guests Windows. Não alterar a implementação FLR do agente, salvo defeito de integração comprovado.

O MVP do agente já foi publicado em `main`, nos commits `e89103d` e `0913d30`.

## Design e contratos existentes

Ler [STUDY_FILE_LEVEL_RESTORE.md](STUDY_FILE_LEVEL_RESTORE.md) para o desenho completo, riscos e não-objetivos. Não duplicar aquele estudo.

O agente Windows expõe endpoints autenticados:

| Operação | Endpoint do agente | Comportamento |
|---|---|---|
| Criar sessão | `POST /restore/flr/sessions` | JSON: `restorePoint` absoluto, `targetBackupId` opcional, `ttlMinutes` opcional (5–1440; padrão 60). Retorna `sessionId`, `expiresAt` e volumes montados. |
| Consultar sessão | `GET /restore/flr/sessions/{id}` | Retorna a sessão ou 404/expirada. |
| Listar diretório | `GET /restore/flr/sessions/{id}/ls?volumeId=...&path=...` | `path` é relativo ao volume retornado; vazio representa a raiz. |
| Baixar arquivo | `GET /restore/flr/sessions/{id}/get?volumeId=...&path=...` | Stream com suporte a HTTP Range. |
| Fechar sessão | `DELETE /restore/flr/sessions/{id}` | Desmonta VHDX e remove o temporário. |

Referências no agente:

- [HyperVBackupAgent.Api/Program.cs](HyperVBackupAgent.Api/Program.cs)
- [HyperVBackupAgent.Infrastructure/FileLevelRestoreService.cs](HyperVBackupAgent.Infrastructure/FileLevelRestoreService.cs)
- [README.md](README.md)

Todos os endpoints do agente, exceto health, usam o bearer token existente. O manager deve usar sua abstração atual de conexão/autenticação com agentes e nunca expor esse token ao browser.

## Direção de implementação no manager

Pontos prováveis de alteração:

- `HyperVaultManager/Services/AgentClient.cs`: chamadas tipadas para criar, consultar, listar, baixar e fechar sessão FLR, seguindo o tratamento de erro e correlation ID existentes.
- `HyperVaultManager/Program.cs`: rotas autenticadas do manager que fazem proxy das operações FLR.
- `HyperVaultManager/wwwroot/js/app.js`: modo `file_level` no fluxo de restore e explorador de arquivos.
- `HyperVaultManager/wwwroot/locales/pt.json` e `en.json`: textos localizados.
- `HyperVaultManager/wwwroot/css/styles.css`: estilos mínimos para o explorador.

Fluxo de UX recomendado:

1. A partir de um restore point selecionado, oferecer **Restore em nível de arquivo** para guests Windows.
2. Criar a sessão no agente usando o restore point e, quando aplicável, o backup alvo.
3. Exibir volumes, breadcrumb, pastas antes de arquivos, nome/tamanho/data e botão de download.
4. Oferecer fechamento explícito da sessão e informar o prazo de expiração automática.
5. Ao receber 404 em browse/download, explicar que a sessão expirou e permitir criar outra.

## Requisitos críticos de proxy e segurança

- O browser chama apenas o manager; o manager adiciona o bearer token ao chamar o agente.
- Usar somente `volumeId` e caminhos relativos. Não criar rotas que aceitem path arbitrário do host para browse/download.
- No download, propagar `Range`, `Content-Range`, `Accept-Ranges`, `Content-Length`, `Content-Type` e `Content-Disposition` quando presentes. Fazer streaming da resposta upstream, sem carregar arquivos inteiros na memória do manager.
- Codificar corretamente `volumeId` e paths relativos, incluindo subdiretórios e espaços.
- Fechar a sessão quando o explorador for fechado; o TTL do agente é contingência.
- Não incluir SMB, mounts read-write, suporte Linux/ext, desbloqueio BitLocker ou ZIP nesta fase.

## Validação

- Compilar/testar o manager com os comandos já existentes no projeto.
- Para integração, usar um agente Windows/Hyper-V com cadeia VHDX real e validar: criar sessão → navegar raiz/subpasta → download com Range → fechar sessão.
- O agente já foi validado com `dotnet build` e 28 testes automatizados.

## Suggested skills

- `github:github` — para obter contexto de repositório, branch ou PR antes de editar/publicar.
- `github:yeet` — para commit, push e criação de PR quando a integração web estiver pronta.
- `github:gh-fix-ci` — se a publicação causar falhas no GitHub Actions.
