using UnityEngine;
using System.Collections;
using System.Collections.Generic;

public class StandingPeopleConcert : MonoBehaviour {

	[HideInInspector] public GameObject planePrefab;
	[HideInInspector] public GameObject circlePrefab;

	[HideInInspector] public GameObject surface;
	[HideInInspector] public Vector2 planeSize = new Vector2(1, 1);

	public GameObject[] peoplePrefabs = new GameObject[0];
	[HideInInspector]
	List<Vector3> spawnPoints = new List<Vector3>();
	[HideInInspector] public GameObject target;
	[HideInInspector] public int peopleCount;


	[HideInInspector] public bool isCircle;
	[HideInInspector] public float circleDiametr = 1;
	[HideInInspector] public bool showSurface = true;

	public enum TestEnum{Rectangle, Circle};
	public TestEnum SurfaceType;

	[HideInInspector]
	public GameObject par;

	public void OnDrawGizmos()
	{		
		if(!isCircle)
			surface.transform.localScale = new Vector3(planeSize.x, planeSize.y, 1);
		else 
			surface.transform.localScale = new Vector3(circleDiametr, circleDiametr, 1);
	}
	
	public void SpawnRectangleSurface()
	{
		if(surface != null) DestroyImmediate(surface);

		var plane = Instantiate(planePrefab, transform.position, Quaternion.identity) as GameObject;
		surface = plane;
		isCircle = false;

		plane.transform.eulerAngles = new Vector3(
    		plane.transform.eulerAngles.x - 90,
    		plane.transform.eulerAngles.y,
   		 	plane.transform.eulerAngles.z
		);

		plane.transform.position += new Vector3(0, 0.01f, 0);
		plane.transform.parent = transform;
		plane.name = "surface";
	}

	public void SpawnCircleSurface()
	{

		if(surface != null) DestroyImmediate(surface);

		var circle = Instantiate(circlePrefab, transform.position, Quaternion.identity) as GameObject;

		isCircle = true;

		circle.transform.eulerAngles = new Vector3(
    		circle.transform.eulerAngles.x - 90,
    		circle.transform.eulerAngles.y,
   		 	circle.transform.eulerAngles.z
		);

		circle.transform.position += new Vector3(0, 0.01f, 0);
		circle.transform.parent = transform;
		circle.name = "surface";
		surface = circle;
	}

	public void RemoveButton()
	{
		if(par != null)
			DestroyImmediate(par);
	}

	public void PopulateButton()
	{
		RemoveButton();

		GameObject parGo = new GameObject();
		par = parGo;
		parGo.transform.parent = gameObject.transform;
		parGo.name = "people";

		spawnPoints.Clear();
		SpawnPeople(peopleCount);
	}



	void SpawnPeople (int _peopleCount) {
	    int[] peoplePrefabIndexes = CommonUtils.GetRandomPrefabIndexes(_peopleCount, ref peoplePrefabs);
		for(int i = 0; i < _peopleCount; i++)
		{			
			Vector3 randomPosition;

			if(!isCircle)
				randomPosition = RandomRectanglePosition();
			else 
				randomPosition = RandomCirclePosition();

			if(randomPosition != Vector3.zero)
			{
                var obj = Instantiate(peoplePrefabs[peoplePrefabIndexes[i]], randomPosition, Quaternion.identity) as GameObject;

		       	RaycastHit hit;

        		if(Physics.Raycast(obj.transform.position + Vector3.up * 0.1f, Vector3.zero, out hit))
        		{
            		obj.transform.position = new Vector3(obj.transform.position.x, hit.point.y, obj.transform.position.z);
       		 	}

				obj.AddComponent<PeopleController>();

				spawnPoints.Add(obj.transform.position);
				if(target != null)
					obj.GetComponent<PeopleController>().SetTarget(target.transform.position);
				else 
					obj.transform.localEulerAngles = new Vector3(obj.transform.rotation.x, transform.rotation.y, obj.transform.rotation.z);

				obj.GetComponent<PeopleController>().animNames = new string[4]{"idle1", "idle2", "cheer", "claphands"};
				obj.transform.parent = par.transform;

                Rigidbody rb = obj.GetComponent<Rigidbody>();
                rb.freezeRotation = true;
			}
		}
	}

	Vector3 RandomRectanglePosition ()
	{
		Vector3 randomPosition = new Vector3(0, 0, 0);

        for(int i = 0; i < 10; i++)
        {
			randomPosition.x = surface.transform.position.x - GetRealPlaneSize().x / 2 + 0.3f + Random.Range(0.0f, GetRealPlaneSize().x - 0.6f);
			randomPosition.z = surface.transform.position.z - GetRealPlaneSize().y / 2 + 0.3f + Random.Range(0.0f, GetRealPlaneSize().y - 0.6f);
			randomPosition.y = surface.transform.position.y;

        	if(IsRandomPositionFree(randomPosition))
        		return randomPosition;
        }

        return Vector3.zero;
	}

	Vector3 RandomCirclePosition ()
	{

		Vector3 center = surface.transform.position;
		float radius = GetRealPlaneSize().x / 2;

        for(int i = 0; i < 10; i++)
        {
			float randomRadius = Random.value * radius;
      		float ang = Random.value * 360;
        	Vector3 pos;
        	pos.x = center.x + randomRadius * Mathf.Sin(ang * Mathf.Deg2Rad);
        	pos.y = center.y;
        	pos.z = center.z + randomRadius * Mathf.Cos(ang * Mathf.Deg2Rad);

        	if(IsRandomPositionFree(pos))
        		return pos;
        }

        return Vector3.zero;
	}

	bool IsRandomPositionFree (Vector3 pos)
	{
		for(int i = 0; i < spawnPoints.Count; i++)
		{
			if(spawnPoints[i].x - 0.6f < pos.x && spawnPoints[i].x + 1 > pos.x && spawnPoints[i].z - 0.5f < pos.z && spawnPoints[i].z + 0.6f > pos.z)
			return false;
		}
		return true;
	}

	Vector2 GetRealPlaneSize()
	{
		Vector3 meshSize = surface.GetComponent<MeshRenderer>().bounds.size;
		return new Vector2(meshSize.x, meshSize.z);
	}

	Vector2 GetRealPeopleModelSize()
	{
		Vector3 meshSize = peoplePrefabs[1].GetComponent<MeshRenderer>().bounds.size;
		return new Vector2(meshSize.x, meshSize.z);
	}

}
