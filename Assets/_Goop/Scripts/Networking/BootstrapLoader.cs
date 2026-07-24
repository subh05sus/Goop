using UnityEngine;
using UnityEngine.SceneManagement;

namespace Goop.Networking
{
    /// <summary>Keeps NetworkManager/services alive across scene loads and hands off to MainMenu.</summary>
    public class BootstrapLoader : MonoBehaviour
    {
        [SerializeField] private string firstScene = "MainMenu";

        private async void Awake()
        {
            DontDestroyOnLoad(gameObject);
            await ServicesBootstrap.InitializeAsync();
            SceneManager.LoadScene(firstScene, LoadSceneMode.Single);
        }
    }
}
