using UnityEngine;
using System.Collections;

public class PeopleController : MonoBehaviour {

	[HideInInspector]
	public float timer;
	[HideInInspector]
	public string[] animNames;

	void Start()
	{
		Tick();


	}

	void Tick()
	{
		timer = 0;
		int randomAnim = Random.Range(0, animNames.Length);
		SetAnimClip(animNames[randomAnim]);
		timer = Random.Range(3.0f, 5.0f);		
	}

	public void SetTarget (Vector3 _target)
	{
       	Vector3 targetPos = new Vector3(_target.x, transform.position.y, _target.z);
		transform.LookAt(targetPos);
	}

	void Update()
	{
		if(timer >= 0)
			timer -= Time.deltaTime;
		else
			Tick();
	}

	public void SetAnimClip(string animName)
	{
		GetComponent<Animator>().CrossFade(animName, 0.1f, 0, Random.Range(0.0f, 1.0f));
	}

}
