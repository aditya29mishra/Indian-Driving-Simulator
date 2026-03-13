using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(LightManager))]
public class StandingLightEditor : Editor
{
    public override void OnInspectorGUI()
    {
        LightManager LM = target as LightManager;

        if (LM.standardCrossroad || LM.TCrossroad)
        {
            EditorGUILayout.HelpBox("Click left mouse button in point", MessageType.Info);
        }
        else
        {
            DrawDefaultInspector();
        }
    }

    public void OnSceneGUI()
    {
        LightManager LM = target as LightManager;

        if (Event.current.type == EventType.MouseMove) SceneView.RepaintAll();

        if (LM.standardCrossroad || LM.TCrossroad)
        {
            EditorGUILayout.HelpBox("Click left mouse button in point", MessageType.Info);
            RaycastHit hit;
            Vector2 mPos = Event.current.mousePosition;
            mPos.y = Screen.height - mPos.y - 40;
            Ray ray = Camera.current.ScreenPointToRay(mPos);

            if (Physics.Raycast(ray, out hit, 3000))
            {
                if ((Event.current.type == EventType.MouseDown && Event.current.button == 0))
                {
                    if (LM.standardCrossroad)
                    {
                        Instantiate(GameObject.Find("Population System").GetComponent<PopulationSystemManager>().standardCrossroad, hit.point, Quaternion.identity);
                        LM.standardCrossroad = false;
                    }

                    else if (LM.TCrossroad)
                    {
                        Instantiate(GameObject.Find("Population System").GetComponent<PopulationSystemManager>().TCrossroad, hit.point, Quaternion.identity);
                        LM.TCrossroad = false;
                    }

                    ActiveEditorTracker.sharedTracker.isLocked = false;
                    DestroyImmediate(LM.gameObject);
                }
            }
        }
    }
}