# Estudo: Restore a Nível de Arquivo (File-Level Restore / "estilo Macrium Reflect")

> Objetivo: avaliar a **viabilidade** de ter uma tela que monta o disco de um backup
> (cadeia full + incrementais) como **somente leitura** em alguma máquina, para
> extrair/copiar arquivos pontuais — igual ao recurso **"Mount backup"** do
> Macrium Reflect.
>
> Escopo: **estudo**. Não há código novo aqui, só análise e recomendação.

---

## TL;DR (veredito)

**É perfeitamente viável e o projeto já tem ~70% dos blocos necessários.**

Hoje o agente já sabe:

1. Materializar uma cadeia (full + incrementais) num **VHDX real** em disco
   (`RestoreMaterializer` / modo `disk_only` do restore).
2. Montar VHDX **somente leitura** via PowerShell (`Mount-VHD -ReadOnly`)
   (`VhdReadOnlyMountValidator`).

O que falta é, essencialmente:

- Um **gerenciador de sessões de montagem** no agente (monta → drive letter → TTL → desmonta).
- Endpoints de **navegação/download** de arquivos na `HyperVBackupAgent.Api`.
- Uma **tela de explorador de arquivos** no `HyperVaultManager` (web).

Ou seja, é uma feature de produto em cima do que já existe, **não** uma reescrita técnica.

---

## 1. O que o Macrium (e Veeam/Bacula/Altaro) fazem

O fluxo "clássico" de File-Level Restore (FLR) a partir de um backup em imagem:

1. Abrir o arquivo de imagem do backup (ou montar o VHDX/VMDK resultante).
2. Expor cada partição do disco como uma **letra de unidade** (ou ponto de montagem),
   em modo **read-only** (ou read-write com *differencing disk* se precisar modificar).
3. O usuário navega pelas pastas no Explorer (ou num browser interno) e copia o que quer.
4. Ao final, **desmonta** e limpa o temporário.

Macrium usa um **driver próprio** (`mr2k`/`MRFlrIdx`) que expõe o `.mrimg` como disco
virtual. Veeam tem o "FLR assistant" + driver de guest. **No nosso caso não precisamos de
driver próprio**: o disco de backup já é **VHDX**, e o Hyper-V (`Mount-VHD`) monta VHDX
nativamente no Windows Server. É exatamente o que o `VhdReadOnlyMountValidator` já executa.

---

## 2. Blocos que JÁ existem no projeto

| Bloco | Onde | Estado |
|---|---|---|
| Materializar cadeia → VHDX real | `Infrastructure/RestoreMaterializer.cs` | ✅ pronto |
| Restore "disk only" (sem criar VM) | `RestoreEngine.cs` (`CreateVm=false`) | ✅ pronto |
| Montar VHDX read-only + desmontar | `VhdReadOnlyMountValidator.cs` (`Mount-VHD -ReadOnly` / `Dismount-VHD`) | ✅ pronto |
| Job async de longa duração na API | `ApiJobService.cs`, `/jobs/*` | ✅ pronto |
| Catálogo de restore points | `FileSystemRestorePointCatalog.cs` | ✅ pronto |
| Wizard de restore no web | `HyperVaultManager/wwwroot/js/app.js` (`restoreForm`) | ✅ pronto (modos `new_vm` / `disk_only`) |

Tudo isso foi validado em host Hyper-V real (`ACC-HYPER-03`, VM `acc-lab-ad`):
materialização do `inc-0001` + `Mount-VHD -ReadOnly` OK + partições enumeradas.
**Ou seja: o "montar read-only" já funciona.** Falta só o **browse/download** por cima.

---

## 3. Arquitetura recomendada

```
┌─────────────────────┐         ┌────────────────────────────┐
│ Browser (operador)  │         │  HyperVaultManager (web)   │
│  Explorer de arqs   │◄────────┤   proxy + UI de FLR        │
└─────────────────────┘  HTTPS  └─────────────┬──────────────┘
                                       Bearer │ X-Correlation-Id
                                             ▼
┌──────────────────────────────────────────────────────────────┐
│ HyperVBackupAgent.Api  (no host Hyper-V — tem admin + Hyper-V)│
│                                                               │
│  /restore/flr/sessions            POST   materializa+monta RO │
│  /restore/flr/sessions/{id}       GET    volumes/drive letters │
│  /restore/flr/sessions/{id}/ls    GET    lista diretório       │
│  /restore/flr/sessions/{id}/get   GET    stream arquivo        │
│  /restore/flr/sessions/{id}       DELETE desmonta+limpa        │
│                                                               │
│  FileLevelRestoreSessionStore  (TTL, auto-unmount, restart-safe)│
│  reusa: RestoreMaterializer + Mount-VHD -ReadOnly              │
└──────────────────────────────────────────────────────────────┘
```

### 3.1 Por que o FLR roda no **agente**, não no web server

- `Mount-VHD` exige **Hyper-V role + admin** local. O agente já roda com isso; o web server (Linux/Docker, no nosso caso — ver `HyperVaultManager/Dockerfile`) **não pode** montar VHDX.
- O VHDX materializado contém dados sensíveis; manter o temporário **dentro do host**
  (já isolado) é mais seguro que copiar o disco inteiro pra outro lugar.
- O web server só **proxia** listagens e o stream de download.

### 3.2 Ciclo de vida de uma sessão FLR

1. `POST /restore/flr/sessions` com `{ restorePointPath, targetBackupId }` →
   - Chama `RestoreMaterializer.MaterializeAsync` num temp dir (ex. `C:\ProgramData\HyperVBackupAgent\flr\<guid>\`).
   - `Mount-VHD -ReadOnly -Passthru` em cada VHDX.
   - Mapeia `Disk → Partition → Volume → DriveLetter/MountPoint` (via
     `Get-Partition`, `Get-Volume`, `Get-Disk` ou `Get-CimInstance Win32_Volume`).
   - Devolve: `sessionId`, `expiresAt`, lista de volumes `[{ drive:"E:", label, fsType:"NTFS", sizeBytes }]`.
2. `GET /sessions/{id}/ls?path=E:\Users\Admin\Docs` →
   `Directory.EnumerateFileSystemEntries` normal (já é Windows montado).
3. `GET /sessions/{id}/get?path=...` →
   stream com `Range` support (para retomar downloads grandes).
4. `DELETE /sessions/{id}` → `Dismount-VHD` + apagar temp dir.
5. **Background**: worker de TTL desmonta sessões ociosas (ex. 45–60 min) e tudo
   órfão no startup do agente.

---

## 4. Variações / alternativas

| Abordagem | Prós | Contras |
|---|---|---|
| **A. Mount-VHD + browse via API (recomendado)** | Reusa 100% do código atual; só funciona Windows guest (NTFS/ReFS); rapidez | Não lê guest Linux nativamente |
| **B. Compartilhar via SMB após montar** | Operador usa Explorer nativo do Windows | Expor SMB; auditoria difícil; security review |
| **C. Montar no web server** | — | Web server é Linux/Docker; não tem Hyper-V. **Descartado** |
| **D. Parser NTFS direto no VHDX (sem mount)** | Portável (roda em qualquer OS) | Reescrita grande (NTFS/ext parser); lentidão |
| **E. iSCSI target do VHDX** | Anexa como disco em outra máquina | Setup pesado demais pra restore pontual |
| **F. Instant VM Recovery (estilo Veeam)** | Boota a VM direto do backup | Muito além do escopo "arquivo pontual" |

Recomendação: **A** para Windows guests (95% dos casos de restore pontual).
**D** só se futuramente houver requisito de FLR com web server Linux — evitaria depender
de Hyper-V, mas é outro projeto.

---

## 5. Casos de borda / riscos

- **Guest Linux (ext4/xfs):** `Mount-VHD` monta o VHDX, mas o Windows não reconhece
  o filesystem das partições → sem drive letter. Para Linux guest há 3 caminhos:
  (a) biblioteca de leitura ext (ex. wrappers de `ntfs-3g`/ext via WinFsp, ou
  bibliotecas como `Ext2Mgr`/driver `WinBtrfs`-style), (b) um pequeno helper
  WSL2 que monta o VHDX e expõe via API, (c) parser userspace. **Todos são
  escopo adicional** — tratar como Fase 3.
- **BitLocker / discos criptografados:** precisaria da chave/credencial pra enxergar
  conteúdo. Fora do escopo inicial.
- **VHDX dinâmico grande:** materialização pode custar muitos GB e I/O. Mitigação:
  reusar o mesmo VHDX entre sessões recentes com cache por `restorePoint+backupId`,
  ou materializar em storage rápido (SSD) configurável.
- **Concorrência:** duas sessões FLR do mesmo VHDX = conflito de mount. O store de
  sessões deve fazer refcount por caminho de VHDX.
- **Cleanup robusto:** qualquer crash deve deixar o host limpo. No boot do agente,
  varrer `flr\<*>` e `Dismount-VHD` em tudo que for órfão.
- **Segurança:** download sai pela API bearer-authenticated → web → browser. Considerar
  *streaming* com `Range`, limite de tamanho opcional, e log de quem baixou o quê.
- **Read-only garantido:** `Mount-VHD -ReadOnly` impõe RO no nível do VHDX. Não há
  risco de o operador sobrescrever o backup por engano. (Modo read-write com
  differencing disk seria uma Fase 4 opcional, "montar, editar e commit".)

---

## 6. Plano de fases sugerido

**Fase 1 — MVP FLR (Windows guest)** — ~80% do valor com 20% do esforço:
- Agente: `FileLevelRestoreService` + `FileLevelRestoreSessionStore` (TTL + cleanup).
- Endpoints `POST /sessions`, `GET /sessions/{id}`, `GET .../ls`, `GET .../get`,
  `DELETE /sessions/{id}`. Reusar `RestoreMaterializer` e `Mount-VHD -ReadOnly`.
- Web: novo modo no wizard (`file_level`), modal de **file explorer**
  (breadcrumb + árvore + lista), botão **Download** (e "Baixar como .zip" depois).

**Fase 2 — Robustez:**
- TTL configurável, auto-unmount, refcount por VHDX, cleanup no startup.
- Log/auditoria de downloads.
- Streaming com `Range` e progresso.

**Fase 3 — Linux guest (ext4/xfs):**
- Helper WinFsp/ext ou WSL2 mount; ou parser userspace.
- Detectar `fsType` não-Windows e mostrar aviso/suporte específico.

**Fase 4 — Opcional:**
- SMB share temporário (alternativa ao browser web).
- Mount read-write com differencing disk ("editar e commit").
- Pesquisa de arquivo por nome dentro do volume.

---

## 7. Conclusão

Sim, é viável e **baixo risco** porque:

- O backup já é VHDX (não formato proprietário).
- O agente já materializa cadeias e já monta VHDX read-only (validado em host real).
- Tudo roda onde precisa rodar (agente = Windows + Hyper-V + admin).

O esforço é concentrado em **produto** (sessões + endpoints + tela de explorador),
não em pesquisa/driver. Para restore pontual de **arquivo em guest Windows**, a
Fase 1 já entrega a experiência equivalente ao "Mount backup" do Macrium.
