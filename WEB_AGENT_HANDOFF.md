# HyperVault Web Agent Handoff

## Objetivo da Proxima Fase

Construir o servidor web/painel que consome a `HyperVBackupAgent.Api` instalada em cada host Hyper-V.

O web server deve falar com a API do agente, nao com o executavel CLI. O CLI fica como ferramenta local de diagnostico e recuperacao manual.

## Estado Atual do Projeto

Repositorio:

```text
C:\Users\felipe\source\repos\limaum87\hypervault
```

Projetos principais:

- `HyperVBackupAgent.Core`
- `HyperVBackupAgent.Infrastructure`
- `HyperVBackupAgent.Cli`
- `HyperVBackupAgent.Api`
- `HyperVBackupAgent.Service`
- `HyperVBackupAgent.Tests`
- `tools/RctTool`

Branch atual observado:

```text
main...origin/main
```

Estado Git observado ao final da validacao:

- sem diferenca versionada pendente;
- `HANDOFF.md` estava nao rastreado;
- este arquivo (`WEB_AGENT_HANDOFF.md`) foi criado para orientar a fase web.

## Estado Funcional Validado

A parte do agente/API para funcionalidades centrais esta pronta para a fase web.

Validado em servidor Hyper-V real:

- cadeia RCT real criada e validada;
- `verify-chain` real via API OK;
- restore real do `inc-0001` materializado via API OK;
- VHDX restaurado lido diretamente OK;
- `Mount-VHD -ReadOnly` no VHDX restaurado OK;
- particoes do VHDX enumeradas OK;
- `backup-incremental` real via API OK, gerando `inc-0002`;
- logs reais com `JobId` e `CorrelationId` OK;
- VM original permaneceu `Running`;
- checkpoints restantes: `0`.

## Cadeia Real Validada

Host Hyper-V observado:

```text
ACC-HYPER-03
```

VM de teste:

```text
acc-lab-ad
```

VM id:

```text
4539d573-67c5-4da1-9750-340459d8b517
```

Disco da VM:

```text
F:\VMs\ACC-LAB-AD\acc-lab-ad.vhdx
```

Backup root usado:

```text
E:\HyperVBackupAgentBackupsNativeRct
```

Cadeia real validada:

```text
E:\HyperVBackupAgentBackupsNativeRct\ACC-HYPER-03\4539d573-67c5-4da1-9750-340459d8b517\chain-20260709-142224
```

Restore points validados:

- `full-20260709-142224`
- `inc-0001`
- `inc-0002`

## Evidencias de Jobs Reais

Jobs importantes executados no servidor real:

- `verify-chain`: `a22996410c1048a6982e75697617bb48`
- `backup-incremental`: `38e7de0cb9d24dd5876c74b6189a549c`
- restore materializado do `inc-0001`: `ddbce6f51f0645ffb574373510cc0def`

Resultado do restore do `inc-0001`:

```text
Destino:
E:\HyperVBackupAgentApiRealOps\restore-inc0001-validation-20260709-181920

VHDX:
E:\HyperVBackupAgentApiRealOps\restore-inc0001-validation-20260709-181920\restore-inc0001-acc-lab-ad.vhdx

Tamanho do VHDX materializado:
267,962,236,928 bytes

Leitura direta:
4096 bytes lidos com sucesso

Mount:
Mount-VHD -ReadOnly OK

Disco virtual:
268,435,456,000 bytes

Particoes:
4
```

## API Para o Servidor Web

Autenticacao:

- Bearer token em todos os endpoints operacionais;
- `/health`, `/health/live` e `/health/ready` sao publicos;
- nao registrar token em logs, commits ou respostas.

Headers importantes:

```http
Authorization: Bearer <token>
X-Correlation-ID: <id-gerado-pelo-web>
Content-Type: application/json
```

O servidor web deve gerar e enviar `X-Correlation-ID` por operacao. A API grava esse valor nos logs e tambem grava `JobId` nos logs dos jobs.

## Endpoints Relevantes

Health e inventario:

- `GET /health`
- `GET /health/live`
- `GET /health/ready`
- `GET /agent`
- `GET /configuration/effective`
- `GET /vms`
- `GET /vms/{id}`
- `GET /vms/{id}/restore-points`

Preflight:

- `POST /backups/preflight`
- `POST /restore/preflight`

Jobs assincronos recomendados para o painel:

- `POST /jobs/backup-full`
- `POST /jobs/backup-incremental`
- `POST /jobs/verify-chain`
- `POST /jobs/verify-restore`
- `POST /jobs/restore`
- `GET /jobs`
- `GET /jobs/{id}`
- `POST /jobs/{id}/cancel`

Endpoints sincronizados existem para diagnostico, mas o painel deve preferir jobs:

- `POST /backups/full`
- `POST /backups/incremental`
- `POST /backups/verify-chain`
- `POST /backups/verify-restore`
- `POST /restore`

## Fluxos Recomendados Para o Site

### Cadastro de Agente

1. Receber host, porta, token e opcionalmente fingerprint do certificado.
2. Chamar `GET /health/live`.
3. Chamar `GET /health/ready`.
4. Chamar `GET /agent`.
5. Chamar `GET /configuration/effective`.
6. Persistir agente como online se `ready`.

### Backup Incremental

1. Usuario seleciona VM.
2. Web chama `POST /backups/preflight`.
3. Se OK, web chama `POST /jobs/backup-incremental`.
4. API retorna `202 Accepted` com `jobId`.
5. Web faz polling em `GET /jobs/{jobId}`.
6. Ao finalizar, web atualiza historico e lista restore points.

Payload base:

```json
{
  "vmNameOrId": "acc-lab-ad",
  "destination": "E:\\HyperVBackupAgentBackupsNativeRct"
}
```

### Verify Chain

```json
{
  "chainPath": "E:\\HyperVBackupAgentBackupsNativeRct\\ACC-HYPER-03\\4539d573-67c5-4da1-9750-340459d8b517\\chain-20260709-142224"
}
```

Use `POST /jobs/verify-chain` e acompanhe por `GET /jobs/{jobId}`.

### Restore

Use `POST /restore/preflight` antes.

Payload base:

```json
{
  "restorePoint": "E:\\HyperVBackupAgentBackupsNativeRct\\ACC-HYPER-03\\4539d573-67c5-4da1-9750-340459d8b517\\chain-20260709-142224",
  "destination": "E:\\HyperVBackupAgentApiRealOps\\restore-target",
  "newName": "restore-test",
  "overwriteExisting": true,
  "targetBackupId": "inc-0001"
}
```

Use `POST /jobs/restore` e acompanhe por `GET /jobs/{jobId}`.

## Contrato de Status de Job

Os jobs persistem em JSON local no agente e sobrevivem a restart.

Campos esperados:

- `jobId`
- `type`
- `status`
- `createdAt`
- `startedAt`
- `completedAt`
- `vmNameOrId`
- `targetPath`
- `resultPath`
- `error`
- `correlationId`
- `message`

Status numericos observados:

- `2`: sucesso;
- `3`: falha.

O site deve tratar tambem nomes textuais se a API evoluir (`Queued`, `Running`, `Succeeded`, `Failed`, `Canceled`).

## Logs

Logs sao JSON via Serilog.

Pasta padrao no Windows:

```text
C:\ProgramData\HyperVBackupAgent\logs
```

Em testes reais, a pasta foi configurada por variavel de ambiente para subpastas em:

```text
C:\HyperVBackupAgentApiRealOps\api-20260709-112809
```

O web server deve sempre exibir ou guardar:

- `jobId`;
- `correlationId`;
- agente/host;
- VM;
- tipo de job;
- horarios de inicio/fim;
- erro textual, se houver.

## Configuracao Real Usada Nos Testes

Variaveis relevantes usadas para rodar a API real:

```text
ASPNETCORE_URLS=http://127.0.0.1:5087
HyperVBackupAgent__ApiToken=<redacted>
HyperVBackupAgent__BackupRoot=E:/HyperVBackupAgentBackupsNativeRct
HyperVBackupAgent__HyperVProvider=PowerShell
HyperVBackupAgent__RctProvider=Native
HyperVBackupAgent__AllowedPathRoots__0=E:/
HyperVBackupAgent__AllowedPathRoots__1=F:/
HyperVBackupAgent__Api__Jobs__StorePath=<jobs-json>
HyperVBackupAgent__Api__Logging__Directory=<logs-dir>
```

Observacao importante:

- `HyperVProvider` real deve ser `PowerShell`.
- `RctProvider` real deve ser `Native`.
- `HyperVProvider=Native` nao e valido neste codigo e cai em simulacao.

## Cuidados Para a Fase Web

- Nao expor token no frontend.
- O backend web deve guardar o token e chamar o agente server-side.
- Operacoes longas devem usar jobs, nunca request HTTP bloqueante ate o fim.
- O painel precisa tolerar jobs longos; restore real do `inc-0001` levou cerca de dezenas de minutos.
- Sempre executar preflight antes de backup/restore.
- Sempre enviar `X-Correlation-ID`.
- Exibir erros de job de forma clara, sem stack trace.
- Tratar `/health/live` diferente de `/health/ready`.
- Nao apagar cadeias reais de backup nos testes do site.
- Evitar iniciar backup incremental se ja houver job em execucao para a mesma VM.
- Verificar checkpoints restantes depois de falhas operacionais.

## Debitos Tecnicos Conhecidos

- Documentacao HTTP/OpenAPI ainda deve ser melhorada.
- Alguns scripts de validacao real ficaram em `artifacts/` ou no host remoto e nao sao parte do produto.
- O `HANDOFF.md` antigo ficou defasado em alguns pontos; este arquivo representa o estado validado mais recente para a fase web.
- Melhorar limpeza automatica de diretorios incrementais parciais apos falha.
- Considerar reduzir detalhes de `changedRanges` em `chain.json` para cadeias grandes.
- Melhorar mensagens de erro quando providers sao configurados incorretamente.

## Validacao Local Recomendada

Antes de novas alteracoes no agente:

```powershell
dotnet build HyperVBackupAgent.sln
dotnet test HyperVBackupAgent.sln --no-build
```

## Notificacao Obrigatoria Para Agentes

Seguir `AGENTS.md`.

Nome do agente:

```text
trunks
```

Notificar via ntfy em caso de:

- sucesso;
- erro;
- bloqueio ou necessidade de decisao do usuario.

Nao encerrar tarefa sem notificar.
