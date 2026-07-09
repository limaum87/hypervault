# HyperVBackupAgent API Tasks

## Objetivo

Preparar a API local do agente para ser consumida com seguranca por um servidor central/web, mantendo o CLI apenas como ferramenta local de diagnostico e operacao manual.

Arquitetura esperada:

```text
Servidor central / painel web
  -> HTTPS + token/API key
  -> HyperVBackupAgent.Api no host Hyper-V
  -> HyperVBackupAgent.Core / Infrastructure
  -> Hyper-V / storage / backups
```

## Status Atual

Ja implementado:

- `GET /health`
- `GET /vms`
- `GET /vms/{id}`
- `GET /vms/{id}/restore-points`
- `POST /backups/full`
- `POST /backups/incremental`
- `POST /backups/verify-chain`
- `POST /backups/verify-restore`
- `POST /restore`
- `POST /maintenance/cleanup-temp-checkpoints`
- `POST /maintenance/apply-retention`
- Autenticacao Bearer Token para todos os endpoints exceto `/health`.
- Logs JSON via Serilog no console.
- Reuso correto dos servicos `Core` e `Infrastructure`, sem chamar o executavel CLI.
- Validacao central de paths absolutos para endpoints que recebem caminhos.
- Respostas de erro em JSON para token ausente/invalido, request invalido, conflito, nao encontrado e erro inesperado.
- Geracao automatica de certificado self-signed local para HTTPS, com endpoint autenticado para expor fingerprint.
- Endpoints de jobs assincronos para backup, verify e restore, com historico JSON local.

## P0 - Necessario Antes de Producao

### 1. Configurar HTTPS real para o agente

Status: implementado inicialmente.

- Definir porta padrao da API.
- Documentar configuracao de certificado.
- Permitir configuracao por `appsettings.json` e variaveis de ambiente.
- Validar inicializacao sem certificado invalido em producao.

Criterio de aceite:

- API sobe em porta fixa com HTTPS.
- Cliente externo consegue chamar `/health`.
- Endpoints protegidos continuam exigindo token.

Observacao:

- A secao `HyperVBackupAgent:Api` controla `ConfigureKestrel`, `HttpPort`, `HttpsPort` e certificado PFX.
- Se `Certificate:AutoGenerate=true` e `Certificate:Path` estiver vazio, o agente cria/reusa um PFX local.
- `GET /agent/certificate` retorna o fingerprint SHA-256 para cadastro no servidor central.
- Em desenvolvimento, `ConfigureKestrel` pode permanecer `false` para usar `launchSettings.json`.

### 2. Validar paths recebidos pela API

Status: implementado inicialmente.

- Criar helper central para normalizar e validar caminhos.
- Bloquear path traversal.
- Bloquear destinos fora dos roots permitidos quando aplicavel.
- Validar `BackupRequest.Destination`, `RestoreRequest.Destination`, `VerifyChainRequest.ChainPath` e `VerifyRestoreRequest.RestorePointPath`.

Criterio de aceite:

- Requisicoes com `..`, caminhos relativos perigosos ou roots proibidos retornam erro 400.
- Backups e restores validos continuam funcionando.

Observacao:

- A configuracao opcional `HyperVBackupAgent:AllowedPathRoots` limita os caminhos aceitos pela API quando preenchida.

### 3. Padronizar respostas de erro

Status: implementado inicialmente.

- Criar formato unico para erros da API.
- Retornar codigos HTTP coerentes:
  - 400 para request invalido.
  - 401 para token ausente/invalido.
  - 404 para VM ou restore point inexistente.
  - 409 para conflito, como VM de destino ja existente.
  - 500 para falha inesperada.
- Evitar vazar stack trace ao cliente.
- Manter detalhes suficientes nos logs.

Criterio de aceite:

- Erros comuns retornam JSON previsivel.
- O painel web consegue exibir mensagem clara ao usuario.

### 4. Transformar operacoes longas em jobs

Status: implementado inicialmente.

Backup, verify-restore e restore podem demorar muito. A API nao deve depender de uma requisicao HTTP aberta ate o fim.

Implementar:

- `POST /jobs/backup-full`
- `POST /jobs/backup-incremental`
- `POST /jobs/verify-chain`
- `POST /jobs/verify-restore`
- `POST /jobs/restore`
- `GET /jobs/{id}`
- `GET /jobs`
- `POST /jobs/{id}/cancel`, se tecnicamente viavel.

Criterio de aceite:

- O endpoint cria um job e retorna `202 Accepted` com `jobId`.
- O servidor web consegue consultar progresso, status final e erro.
- Operacoes existentes podem permanecer como chamadas sincronas para diagnostico, mas o painel deve usar jobs.

### 5. Persistir historico de jobs

Status: implementado inicialmente com JSON local.

- Registrar jobs localmente em arquivo JSON ou SQLite opcional.
- Persistir:
  - `job_id`
  - tipo
  - VM
  - status
  - inicio
  - fim
  - progresso quando disponivel
  - erro
  - caminho da cadeia ou restore point gerado
- Permitir reconstruir o historico apos reiniciar o servico.

Criterio de aceite:

- Jobs recentes continuam disponiveis apos restart do agente.
- O painel consegue listar ultimas execucoes.

## P1 - Integracao Com Servidor Central

### 6. Endpoint de informacoes do agente

Adicionar:

- `GET /agent`

Retornar:

- versao do agente
- hostname
- sistema operacional
- modo do provider Hyper-V
- modo do provider RCT
- backup root configurado
- scheduler habilitado/desabilitado

Criterio de aceite:

- Servidor central consegue inventariar agentes.

### 7. Endpoint de configuracao efetiva

Adicionar:

- `GET /configuration/effective`

Nao retornar segredos. Mas retornar configuracoes operacionais uteis:

- backup root
- simulation root, quando aplicavel
- scheduler
- retention
- providers

Criterio de aceite:

- Token/API key nunca aparece na resposta.

### 8. Melhorar listagem de restore points

- Confirmar contrato `GET /vms/{id}/restore-points`.
- Incluir chain id, backup id, tipo, data, status, tamanho e caminho.
- Permitir filtro por status e intervalo de datas.

Criterio de aceite:

- Painel consegue montar tela de restore sem varrer detalhes manualmente.

### 9. Endpoint para validar pre-flight

Adicionar:

- `POST /backups/preflight`
- `POST /restore/preflight`

Validar antes de executar:

- VM existe.
- RCT disponivel quando incremental.
- destino acessivel.
- espaco livre suficiente quando possivel.
- token/configuracao presentes.
- VM de restore nao conflita, salvo overwrite explicito.

Criterio de aceite:

- Painel consegue avisar problemas antes de iniciar job longo.

## P2 - Operacao e Observabilidade

### 10. Logs de producao

- Configurar sink de arquivo com rotacao.
- Definir pasta padrao de logs.
- Incluir correlation id por request/job.
- Documentar coleta dos logs.

Criterio de aceite:

- Cada job pode ser rastreado nos logs pelo `jobId`.

### 11. Health checks mais completos

Adicionar:

- `GET /health/live`
- `GET /health/ready`

Ready deve validar:

- configuracao minima
- acesso ao backup root
- provider Hyper-V inicializavel

Criterio de aceite:

- Monitoramento externo diferencia processo vivo de agente pronto para operar.

### 12. Documentar contrato HTTP

- Criar exemplos de request/response.
- Documentar autenticacao.
- Documentar codigos de erro.
- Documentar fluxo recomendado para backup e restore usando jobs.
- Opcional: habilitar Swagger/OpenAPI em ambiente controlado.

Criterio de aceite:

- Servidor web consegue integrar sem ler o codigo da API.

## P3 - Seguranca Avancada

### 13. Rotacao de token

- Permitir token atual e token novo durante janela de troca.
- Documentar procedimento.

### 14. Registro do agente no servidor central

- Criar fluxo futuro de enrollment.
- Trocar token manual por credencial emitida pelo servidor central.
- Usar fingerprint SHA-256 do certificado local no primeiro cadastro do agente.

### 15. Considerar mTLS

- Avaliar mTLS para comunicacao agente-servidor.
- Documentar requisitos de certificado.

## Decisoes Arquiteturais

- O servidor web deve conversar com `HyperVBackupAgent.Api`, nao com o executavel CLI.
- O CLI deve continuar existindo para teste local, diagnostico e recuperacao manual.
- JSON deve continuar sendo o formato portavel oficial da cadeia de backup.
- SQLite pode ser usado depois como indice local para jobs, inventario e consultas rapidas, mas nao deve substituir os arquivos JSON da cadeia.

## Ordem Recomendada

1. Validacao de paths.
2. Respostas de erro padronizadas.
3. HTTPS/porta/certificado documentados.
4. Modelo de jobs assincronos.
5. Historico persistido de jobs.
6. Endpoints `/agent` e `/configuration/effective`.
7. Logs de arquivo com correlation id.
8. Documentacao HTTP/OpenAPI.
