Desenvolver um servidor central para Linux que gerencie múltiplos agentes HyperVBackupAgent instalados em hosts Hyper-V, permitindo cadastrar hosts, listar VMs, criar jobs de backup, acompanhar histórico, verificar backups e disparar restore.

Stack sugerida:


Docker Compose para rodar no Linux.

Para o MVP, prefira simplicidade:

frontend web simples html5 + css+ javascript + chamadas de api
banco de dados pode ser sqlite ne , nao vai ter milhoes de acesso 
Docker Compose

Funcionalidades principais:

Cadastro de hosts Hyper-V:
nome
IP/FQDN
porta da API do agente
token/API key
status
Testar conexão com agente:
chamar GET /health
Sincronizar VMs:
chamar GET /vms no agente
armazenar VMs no banco
Cadastro de storages:
nome
tipo: local_path, smb
caminho
observações
Criação de jobs de backup:
VM
host
storage destino
horário
retenção em dias
tipo: full inicialmente
Execução manual de backup:
chamar POST /backups/full no agente
Histórico de backups:
VM
host
data/hora
tipo
status
tamanho
duração
mensagem de erro
Verificação de backup:
chamar POST /backups/verify no agente
Restore:
escolher backup
escolher host destino
restaurar com novo nome
nunca sobrescrever VM sem confirmação explícita
Dashboard:
hosts online/offline
últimos backups
falhas recentes
VMs sem backup
uso estimado de storage

Estrutura sugerida:

HyperVBackupManager.Web
HyperVBackupManager.Core
HyperVBackupManager.Infrastructure
HyperVBackupManager.Worker

Entidades principais:

HyperVHost
VirtualMachine
StorageTarget
BackupJob
BackupRun
RestoreRun
VerificationRun

APIs internas:

GET /hosts
POST /hosts
POST /hosts/{id}/sync-vms
GET /vms
POST /jobs
POST /jobs/{id}/run-now
GET /backups
POST /backups/{id}/verify
POST /backups/{id}/restore

Requisitos importantes:

O Manager não deve acessar Hyper-V diretamente.
Toda operação Hyper-V deve ser feita pelo agente Windows.
Comunicação Manager → Agent via HTTPS.
Token por host no MVP.
Logs estruturados.
Tratamento claro de falhas.
Jobs agendados usando Hangfire, Quartz.NET ou BackgroundService.
Criar Dockerfile e docker-compose.yml.
Criar README.md com instruções de instalação no Linux.

Limitações aceitas no MVP:

Apenas backup full.
Apenas storage SMB/local path.
Sem RCT/incremental ainda.
Sem multiusuário avançado.
Sem criptografia de backup no início.

Preparar arquitetura para futuro:

backup incremental via RCT
retenção por cadeia full + incrementais
múltiplos storages
S3
alertas Nagios/webhook/email
restore para host diferente
verify-chain e verify-restore