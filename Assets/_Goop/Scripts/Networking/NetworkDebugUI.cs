using Unity.Netcode;
using UnityEngine;

namespace Goop.Networking
{
    /// <summary>Temporary IMGUI host/client/server buttons for M1 testing. Removed once Lobby UI (M2) exists.</summary>
    public class NetworkDebugUI : MonoBehaviour
    {
        private void OnGUI()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null) return;

            GUILayout.BeginArea(new Rect(10, 10, 220, 150));
            if (!nm.IsClient && !nm.IsServer)
            {
                if (GUILayout.Button("Start Host")) nm.StartHost();
                if (GUILayout.Button("Start Client")) nm.StartClient();
                if (GUILayout.Button("Start Server")) nm.StartServer();
            }
            else
            {
                GUILayout.Label($"Mode: {(nm.IsHost ? "Host" : nm.IsServer ? "Server" : "Client")}");
                GUILayout.Label($"Connected clients: {nm.ConnectedClients.Count}");
                if (GUILayout.Button("Shutdown")) nm.Shutdown();
            }
            GUILayout.EndArea();
        }
    }
}
