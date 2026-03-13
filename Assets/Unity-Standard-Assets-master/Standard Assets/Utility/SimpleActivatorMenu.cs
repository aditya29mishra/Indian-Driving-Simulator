using UnityEngine;
using UnityEngine.UI;

namespace MyProject
{
    public class SimpleActivatorMenu : MonoBehaviour
    {
        public Text camSwitchButton;
        public GameObject[] objects;

        private int m_CurrentActiveObject;

        private void OnEnable()
        {
            m_CurrentActiveObject = 0;
            UpdateUI();
        }

        public void NextCamera()
        {
            int nextActiveObject = (m_CurrentActiveObject + 1) % objects.Length;

            for (int i = 0; i < objects.Length; i++)
            {
                objects[i].SetActive(i == nextActiveObject);
            }

            m_CurrentActiveObject = nextActiveObject;
            UpdateUI();
        }

        private void UpdateUI()
        {
            if (camSwitchButton != null && objects.Length > 0)
            {
                camSwitchButton.text = objects[m_CurrentActiveObject].name;
            }
        }
    }
}