using UnityEngine;
using System.Collections;

public class SemaphorePeople : MonoBehaviour
{
    private int howManyInMe;
    [SerializeField]private bool carCan;
    [SerializeField]private bool peopleCan;
    private bool flicker;

    public bool CAR_CAN
    {
        get { return carCan; }
        set { carCan = value; }
    }

    public bool PEOPLE_CAN
    {
        get { return peopleCan; }
        set { peopleCan = value; }
    }

    public int HOW_MANY
    {
        get { return howManyInMe; }
        private set { }
    }

    public bool FLICKER
    {
        get { return flicker; }
        set { flicker = value; }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("People"))
        {
            howManyInMe++;
        }
    }

    private void OnTriggerStay(Collider other)
    {
        if (other.CompareTag("People"))
        {
            if(other.transform.GetComponent<Passersby>())
            {
                Passersby people = other.GetComponent<Passersby>();
                people.INSIDE = true;

                if(!peopleCan)
                {
                    people.RED = true;
                }
                else
                {
                    people.RED = false;
                }
            }
        }

        if (other.CompareTag("Car"))
        {
            if (other.transform.GetComponent<CarAIController>())
            {
                CarAIController car = other.GetComponent<CarAIController>();
                car.INSIDE = true;
            }
        }

        if (other.transform.CompareTag("Bcycle"))
        {
            if (other.transform.GetComponentInParent<BcycleGyroController>())
            {
                BcycleGyroController bcycle = other.GetComponentInParent<BcycleGyroController>();
                bcycle.insideSemaphore = true;
            }
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (other.CompareTag("Car"))
        {
            if (other.transform.GetComponent<CarAIController>())
            {
                CarAIController car = other.GetComponent<CarAIController>();
                car.INSIDE = false;
            }
        }

        if (other.transform.CompareTag("Bcycle"))
        {
            if (other.transform.GetComponentInParent<BcycleGyroController>())
            {
                BcycleGyroController bcycle = other.GetComponentInParent<BcycleGyroController>();
                bcycle.insideSemaphore = false;
            }
        }

        if (other.CompareTag("People"))
        {
            if(other.transform.GetComponent<Passersby>())
            {
                Passersby people = other.GetComponent<Passersby>();
                StartCoroutine(StopInside(people));
            }
        }

        if (other.CompareTag("People"))
        {
            howManyInMe--;
        }
    }

    IEnumerator StopInside(Passersby passersby)
    {
        yield return new WaitForSeconds(1.0f);

        passersby.INSIDE = false;
        passersby.RED = false;
        passersby.ANIMATION_STATE = passersby.LastState;
    }
}