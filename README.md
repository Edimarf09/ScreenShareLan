# ScreenShareLan

Compartilhamento de tela (vídeo) em LAN / LAN virtual (ex.: Radmin VPN), **tudo UDP**,
com modelo de **sala**: o host roda o servidor e **qualquer um na sala (host ou convidado)
pode compartilhar a tela**.

## As 3 opções do menu

- **Hostear** — vira o servidor da sala e já te coloca dentro dela. Passe seu `IP:porta`
  pro pessoal (ou deixe eles acharem pela "Lista da LAN").
- **Lista da LAN** — mostra as salas abertas na rede (LAN física e virtual). Dois cliques pra entrar.
- **Conexão direta** — digita `ip:porta` (ex.: `26.10.20.30:45679`) e entra.

## Compartilhar a tela

Dentro da sala, o botão **"Compartilhar tela"** abre a escolha de qualidade. São **4 opções
travadas** (pra otimizar):

- 720p 30FPS
- 720p 60FPS
- 1080p 30FPS
- 1080p 60FPS

Um de cada vez transmite; quem aperta compartilhar assume a transmissão. Todo mundo vê.

## Como funciona (resumo técnico)

- **Descoberta:** o servidor manda um *broadcast* UDP (porta 45678) em todas as interfaces,
  inclusive na faixa da LAN virtual (ex.: `26.255.255.255` do Radmin). A "Lista da LAN" escuta isso.
- **Sala (porta 45679, UDP):** controle (entrar / keepalive / sair / iniciar / parar) + vídeo.
- **Vídeo:** cada frame é capturado (GDI), reduzido pra resolução do preset e virado **JPEG**.
  Como não cabe num pacote UDP, o frame é **fragmentado** em pedaços de ~1200 bytes e **remontado**
  do outro lado. Cada frame é um JPEG independente: se perder pacote, aquele frame é descartado e o
  próximo entra limpo (sem travar / sem artefato acumulado).
- **Relay:** quem compartilha manda o vídeo pro servidor, que **repassa** pra todos os outros.
  Assim todo mundo só precisa alcançar o host (que é o pré-requisito de entrar na sala).
- O host participa conectando um cliente em `127.0.0.1`, então host e convidados usam o mesmo código.

## Compilar e gerar o .exe

Precisa do **.NET SDK 10** no Windows.

```bash
# build/teste rápido
dotnet build -c Release

# gerar UM .exe só, sem precisar de .NET instalado na máquina dos amigos:
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

O `.exe` sai em:
`bin/Release/net10.0-windows/win-x64/publish/ScreenShareLan.exe`

(É esse arquivo que você manda pros amigos. Se preferir menor e não se importar que eles
tenham o .NET Runtime instalado, tire o `--self-contained true`.)

## Pontos de atenção no Windows

- **Firewall:** na primeira vez o Windows vai perguntar se libera o app. Tem que **permitir**
  (rede privada), senão o UDP não passa.
- **DPI/escala:** com escala do Windows diferente de 100% a captura pode sair com tamanho
  estranho. O projeto já pede `PerMonitorV2`. Se der problema, dá pra trocar pra `SystemAware`.
- **Multi-monitor:** captura só o monitor **primário** por enquanto.
- **Sem áudio:** é só vídeo, como combinado.
- **Banda:** o relay gasta `bitrate × nº de espectadores` de subida no host. Em 1080p60 isso é
  pesado — tranquilo em LAN cabeada, mas na VPN (internet) pode não segurar; nesse caso use 720p.
- **Desempenho real:** GDI + JPEG dá conta bem de **720p** e **1080p30**. Para **1080p60 de
  verdade**, o caminho é trocar a captura por **Desktop Duplication (DXGI)** e o encode por
  **hardware (NVENC / QuickSync / AMF)** com H.264. É o próximo passo natural se precisar.

## Estrutura

```
ScreenShareLan.csproj
Program.cs               # entry point
MainForm.cs              # menu das 3 opções
RoomForm.cs              # janela da sala (lista + vídeo + botão compartilhar)
LanListForm.cs           # descoberta de salas na LAN
Core/
  Net.cs                 # protocolo, tipos de msg, presets, utils de rede
  ScreenCapture.cs       # captura + escala + JPEG + cursor
  RoomServer.cs          # servidor UDP (relay + roster + anúncio)
  RoomClient.cs          # participante UDP (entra, compartilha, recebe)
```

> Observação: o código foi escrito e revisado, mas **não foi compilado** neste ambiente
> (WinForms só builda no Windows). Trate como uma base sólida pra buildar e testar aí.
