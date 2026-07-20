Problema

No backup incremental, o agente lança DirectoryNotFoundException ("Could not find a part of the path 'e:\Vault\ACC-HYPER-03\<vmId>'") quando ainda não existe nenhum backup full dessa VM no storage.

Arquivo: HyperVBackupAgent.Infrastructure/BackupEngine.cs
Método: RunIncrementalBackupAsync

Linha que quebra:
var vmRoot = Path.Combine(Path.GetFullPath(request.Destination), _hostName, vm.Id);
var chainDirectory = Directory.EnumerateDirectories(vmRoot, "chain-*").OrderBy(path => path).LastOrDefault()
    ?? throw new InvalidOperationException($"No full backup chain found for VM '{vm.Name}'.");

O Directory.EnumerateDirectories(vmRoot, ...) lança exceção quando vmRoot não existe, antes de chegar no ?? throw. Por isso o usuário vê "Could not find a part of the path" em vez da mensagem amigável.
Por que o agente não cria a pasta

    Em full ele cria tudo (Directory.CreateDirectory / _storage.EnsureDirectoryAsync), pois só precisa gravar.
    Em incremental não há o que criar: ele precisa ler um full anterior para calcular o delta via RCT. Sem full, não há base.

Comportamento desejado (auto-fallback incremental → full)

Quando um incremental for solicitado e não existir chain de full para a VM, o agente deve executar automaticamente um full e retornar normalmente (mesmo resultado/estrutura de um full). É o padrão de produtos como Veeam/Nakivo — o primeiro backup de um job incremental vira full.
Mudança mínima

Em RunIncrementalBackupAsync, antes de enumerar as chains, verificar se existe full:

    Trocar a enumeração "crua" por algo que não lance quando vmRoot não existe:
    var chainDirectory = Directory.Exists(vmRoot)
        ? Directory.EnumerateDirectories(vmRoot, "chain-*").OrderBy(path => path).LastOrDefault()
        : null;
    Se chainDirectory for nulo → chamar RunFullBackupAsync(request, cancellationToken) e retornar o resultado dela (em vez de estourar exceção).Manter o fluxo incremental normal quando existir a chain.

Observações para o agente

    Não há necessidade de criar a pasta no incremental em outro cenário; o auto-fallback cobre o caso real (primeiro backup de um job incremental).
    O resultado do full-fallback já é compatível com o que a API/manager espera (BackupResult com Type = Full), e os próximos incrementais vão encontrar a chain criada.
    Adicionar/cobrir com teste em HyperVBackupAgent.Tests/BackupEngineTests.cs: "incremental sem chain vira full".