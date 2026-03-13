
using UnityEngine;


namespace EVP
{

public class CustomGravity : MonoBehaviour
	{
	public float gravityAccel = 9.81f;
	public Vector3 gravityDir = -Vector3.up;
	public bool localDownDirection = false;

	Rigidbody m_rigidbody;
	VehicleController m_vehicle;


	void OnEnable ()
		{
		m_rigidbody = GetComponentInParent<Rigidbody>();
		m_vehicle = GetComponentInParent<VehicleController>();

		m_rigidbody.useGravity = false;
		m_vehicle.useCustomGravity = true;
		m_vehicle.customGravity = GetGravity();
		}


	void OnDisable ()
		{
		m_rigidbody.useGravity = true;
		m_vehicle.useCustomGravity = false;
		}


	void FixedUpdate ()
		{
		Vector3 gravity = GetGravity();
		m_rigidbody.AddForce(gravity, ForceMode.Acceleration);
		m_vehicle.customGravity = gravity;
		}


	Vector3 GetGravity ()
		{
		return localDownDirection? -gravityAccel * transform.up : gravityAccel * gravityDir.normalized;
		}
	}

}