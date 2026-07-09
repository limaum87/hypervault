Crie um projeto chamado HyperVBackupAgent em C#/.NET 8 ou superior.

Objetivo:
Desenvolver um agente Windows para hosts Hyper-V capaz de executar backup full, backup incremental via Hyper-V RCT, verificação de cadeia e restore de VMs. O agente deve rodar localmente no host Hyper-V e futuramente ser controlado por um servidor central Linux.

Contexto:
O projeto deve ser uma alternativa leve ao Veeam para ambientes pequenos, focando em:

* backup diário de VMs Hyper-V;
* full semanal;
* incrementais diários usando RCT;
* retenção de 7, 15 ou 30 dias;
* restore simples no próprio host ou em outro host Hyper-V;
* verificação automática da cadeia de backups.

Requisitos técnicos:

* Linguagem: C#.
* Runtime: .NET 8 ou superior.
* Sistema: Windows Server com Hyper-V.
* Rodar como Windows Service.
* Ter também uma CLI para testes locais.
* Expor API HTTPS local.
* Autenticação inicial por token/API key.
* Logs estruturados em JSON.
* Persistência local em SQLite.
* Preparar código para execução sem interface gráfica.

Estrutura sugerida:

* HyperVBackupAgent.Service
* HyperVBackupAgent.Cli
* HyperVBackupAgent.Api
* HyperVBackupAgent.Core
* HyperVBackupAgent.Infrastructure
* HyperVBackupAgent.Tests

Criar interfaces:

* IHyperVService
* IRctService
* IBackupEngine
* IRestoreEngine
* IVerifyEngine
* IStorageProvider
* IMetadataRepository
* IHashService

Funcionalidades do MVP:

1. Listar VMs do host Hyper-V.
2. Obter detalhes da VM:

   * nome
   * id
   * estado
   * geração
   * memória
   * discos VHDX/AVHDX
   * caminho dos discos
   * tamanho virtual
   * tamanho físico
   * checkpoints existentes
3. Validar se a VM suporta backup com RCT.
4. Criar Production Checkpoint temporário antes do backup.
5. Fazer backup full inicial da VM.
6. Fazer backup incremental usando RCT.
7. Salvar metadata da cadeia de backup.
8. Verificar a integridade da cadeia.
9. Restaurar um ponto de backup reconstruindo o VHDX.
10. Remover checkpoint temporário após backup.
11. Em caso de erro, tentar limpar checkpoint temporário.
12. Nunca sobrescrever VM existente sem flag explícita.

Importante sobre RCT:
O projeto deve usar o mecanismo Resilient Change Tracking do Hyper-V para descobrir blocos alterados desde o último backup.

Implementar uma camada IRctService responsável por:

* verificar se RCT está disponível para a VM/disco;
* obter o identificador de mudança atual;
* comparar mudanças entre o último backup e o estado atual;
* retornar os ranges/blocos alterados de cada VHDX;
* persistir informações necessárias para o próximo incremental.

O código deve tentar usar APIs nativas do Hyper-V/WMI/CIM quando possível. Caso alguma parte do RCT não seja diretamente acessível via .NET, isolar essa dependência em uma classe específica para permitir implementação posterior com:

* PowerShell;
* WMI/CIM;
* chamada nativa;
* biblioteca externa.

Formato de backup:
Criar uma estrutura de arquivos simples e documentada:

/backups/
{host}/
{vm-id}/
chain-{yyyyMMdd-HHmmss}/
chain.json
full/
disk-0.full.vhdx
disk-1.full.vhdx
metadata.json
increments/
inc-0001/
inc.json
disk-0.blocks
disk-1.blocks
inc-0002/
inc.json
disk-0.blocks
disk-1.blocks

O full pode ser uma cópia completa dos VHDX em estado consistente.

O incremental deve armazenar:

* identificação do disco;
* tamanho lógico do disco;
* block size usado;
* lista de ranges alterados;
* dados brutos dos blocos alterados;
* checksum por bloco ou por chunk;
* checksum geral do arquivo incremental;
* change tracking id inicial;
* change tracking id final;
* data/hora.

Metadata mínima:
chain.json:

* chain_id
* vm_id
* vm_name
* source_host
* created_at
* latest_restore_point
* full_backup_id
* restore_points
* disks
* retention_policy
* status

full metadata:

* backup_id
* type: full
* created_at
* vm_id
* vm_name
* disks
* files
* hashes
* size_bytes
* rct_reference_ids
* status

incremental metadata:

* backup_id
* type: incremental
* parent_backup_id
* created_at
* vm_id
* vm_name
* disks
* changed_ranges
* files
* hashes
* start_change_id
* end_change_id
* status

Fluxo de backup full:

1. Validar VM.
2. Criar Production Checkpoint temporário.
3. Identificar discos consistentes para leitura.
4. Copiar VHDX completo para storage.
5. Calcular hashes.
6. Salvar metadata.
7. Salvar referência RCT/change id para futuros incrementais.
8. Remover checkpoint temporário.
9. Registrar status.

Fluxo de backup incremental:

1. Validar existência de full anterior.
2. Criar Production Checkpoint temporário.
3. Consultar RCT para descobrir blocos alterados desde o último backup.
4. Ler somente os ranges alterados do VHDX consistente.
5. Gravar arquivo incremental contendo ranges + dados.
6. Calcular hashes.
7. Atualizar chain.json.
8. Salvar novo change id.
9. Remover checkpoint temporário.
10. Registrar status.

Fluxo de restore:

1. Escolher restore point.
2. Validar cadeia full + incrementais necessários.
3. Criar diretório temporário de restore.
4. Copiar full VHDX para destino temporário.
5. Aplicar incrementais em ordem.
6. Validar tamanho e hashes possíveis.
7. Criar nova VM no Hyper-V usando o VHDX reconstruído.
8. Permitir restore com novo nome.
9. Bloquear sobrescrita por padrão.

Verificação:
Implementar dois níveis:

verify-chain:

* valida chain.json;
* valida existência dos arquivos;
* valida hashes;
* valida sequência full + incrementais;
* valida parent_backup_id;
* valida se nenhum incremento está faltando.

verify-restore:

* executa verify-chain;
* reconstrói o VHDX em diretório temporário;
* tenta montar o VHDX em modo read-only, se possível;
* desmonta o VHDX;
* remove temporários, salvo flag para manter.

CLI:
Criar comandos:

* hvbackup-agent list-vms
* hvbackup-agent vm-info --vm "ERP01"
* hvbackup-agent backup-full --vm "ERP01" --destination "\backup\hyperv"
* hvbackup-agent backup-inc --vm "ERP01" --destination "\backup\hyperv"
* hvbackup-agent verify-chain --vm "ERP01" --chain-id "..."
* hvbackup-agent verify-restore --vm "ERP01" --restore-point "..."
* hvbackup-agent restore --restore-point "..." --new-name "ERP01-Restore-Test"
* hvbackup-agent cleanup-temp-checkpoints
* hvbackup-agent list-restore-points --vm "ERP01"

API HTTPS:

* GET /health
* GET /vms
* GET /vms/{id}
* GET /vms/{id}/restore-points
* POST /backups/full
* POST /backups/incremental
* POST /backups/verify-chain
* POST /backups/verify-restore
* POST /restore
* POST /maintenance/cleanup-temp-checkpoints

Storage:
Implementar inicialmente:

* local path
* SMB path

Criar interface IStorageProvider para permitir no futuro:

* SFTP
* S3
* NFS
* Azure Blob

Retenção:
Implementar política inicial:

* manter últimas N cadeias;
* manter restore points por N dias;
* nunca apagar uma cadeia se ela estiver incompleta sem registrar alerta;
* nunca apagar o último full válido.

Segurança:

* API com token obrigatório.
* Não expor endpoints sem autenticação, exceto /health opcional.
* Validar caminhos para evitar path traversal.
* Não permitir comandos arbitrários vindos da API.
* Não sobrescrever VM sem flag explicitamente true.

Tratamento de falhas:

* Se backup falhar, marcar metadata como failed.
* Se checkpoint temporário existir, tentar remover.
* Se remoção falhar, registrar alerta crítico.
* Se incremental falhar, não atualizar ponteiro de último backup.
* Se restore falhar, manter logs suficientes para diagnóstico.
* Operações devem ser idempotentes quando possível.

Testes:
Criar testes unitários para:

* parsing de metadata;
* validação de cadeia;
* aplicação de incrementais em arquivo fake;
* verificação de hashes;
* retenção.

Criar também modo de simulação:

* usar arquivos fake em vez de VHDX;
* simular changed ranges;
* testar full + incremental + restore sem Hyper-V real.

Documentação:
Criar README.md com:

* objetivo do projeto;
* arquitetura;
* como instalar;
* como rodar como serviço;
* exemplos de CLI;
* formato de backup;
* fluxo full;
* fluxo incremental via RCT;
* fluxo restore;
* verify-chain;
* verify-restore;
* limitações conhecidas.

Limitações aceitas no MVP:

* sem interface web;
* sem deduplicação global;
* sem compressão avançada;
* sem criptografia inicialmente;
* sem restore granular de arquivos;
* sem suporte a cluster Hyper-V;
* sem suporte avançado a aplicações como SQL/Exchange além do Production Checkpoint.

Prioridade de implementação:

1. Estrutura do projeto.
2. CLI.
3. Listagem de VMs.
4. Backup full.
5. Metadata e verify-chain.
6. Restore de full.
7. Modo simulado de incremental.
8. Interface IRctService.
9. Implementação real de incremental via RCT.
10. verify-restore.
11. Windows Service.
12. API HTTPS.
