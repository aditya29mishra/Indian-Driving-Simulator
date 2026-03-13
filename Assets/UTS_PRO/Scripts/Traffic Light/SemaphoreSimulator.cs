using UnityEngine;

public class SemaphoreSimulator : MonoBehaviour
{
    private float greenTimer;
    private float yellowTimer;
    private float redTimer;
    private float peopleTimer;
	private int stage;
    private bool yellowOn;	
	private bool timeBreak;
    private bool timePeople;

    [SerializeField] [Tooltip("Selecting the direction of the initial motion at the traffic light / Выбор направления начального движения на светофоре")] private bool blockForward;
    [SerializeField] [Tooltip("Traffic lights for transport on one side / Светофоры для транспорта с одной стороны")] private TLGraphicsControl[] FWDlights;
    [SerializeField] [Tooltip("Traffic lights for transport on the other side / Светофоры для транспорта с другой стороны")] private TLGraphicsControl[] LRlights;
    [SerializeField] [Tooltip("Traffic lights for pedestrians on one side / Светофоры для пешеходов с одной стороны")] private TLGraphicsControl[] FWDpeopleLight;
    [SerializeField] [Tooltip("Traffic lights for pedestrians on the other side / Светофоры для пешеходов с другой стороны")] private TLGraphicsControl[] LRpeopleLight;
    [SerializeField] [Tooltip("Triggers of traffic lights on one side / Тригеры светофоров с одной стороны")] private SemaphorePeople[] FWDpeopleZebra;
    [SerializeField] [Tooltip("Triggers of traffic lights on the other side / Тригеры светофоров с другой стороны")] private SemaphorePeople[] LRpeopleZebra;
    [SerializeField] [Tooltip("Time for green light / Время для зеленого света")] private float greenTime;
    [SerializeField] [Tooltip("Time for yellow light / Время для желтого света")] private float yellowTime;
    [SerializeField] [Tooltip("Time for red light / Время для красного света")] private float redTime;
    [SerializeField] [Tooltip("Time for pedestrians / Время для пешеходов")] private float peopleTime;

    public bool YELLOW_ON
    {
        get { return yellowOn; }
        set
        {
            yellowOn = value;
            YellowTime();
        }
    }

    public int STAGE
    {
        get { return stage; }
        set
        {
            stage = value;
        }
    }       

    private void Awake()
    {
        greenTimer = greenTime;
        yellowTimer = yellowTime;
        redTimer = redTime;
        peopleTimer = peopleTime;
    }

    private void Start()
    {
        for (int i = 0; i < FWDpeopleLight.Length; i++)
        {
            FWDpeopleLight[i].DisableGreen(false);
            FWDpeopleLight[i].EnableRed();
        }

        for (int i = 0; i < LRpeopleLight.Length; i++)
        {
            LRpeopleLight[i].DisableGreen(false);
            LRpeopleLight[i].EnableRed();
        }

        SanityCheck();
        SetFlow();
    }

    private void SanityCheck()
    {
        if(blockForward)
        {
            if(LRlights.Length == 0)
            {
                blockForward = false;
            }
        }
        else
        {
            if(FWDlights.Length == 0)
            {
                blockForward = true;
            }
        }
    }

	private void Update()
	{
       if (yellowOn)
        {
            yellowTimer -= Time.deltaTime;

            if (yellowTimer <= 0)
            {
                yellowOn = false;
                yellowTimer = yellowTime;

                if (timeBreak)
                {
                    if(stage == 0)
                    {
					    stage++;
                    }
                    else
                    {
                        stage = 0;
                    }

                    timeBreak = false;
                    SetFlow();
                    greenTimer = greenTime;

                }
                else
                {
					if (blockForward)
					{
						for (int i = 0; i < LRlights.Length; i++)
						{
							LRlights[i].DisableYellow();
							LRlights[i].EnableRed();
						}
					}
					else
					{
						for (int i = 0; i < FWDlights.Length; i++)
						{
							FWDlights[i].DisableYellow();
							FWDlights[i].EnableRed();
						}
					}

					if(stage == 0)
					{
						timeBreak = true;
					}
					else if(stage == 1)
					{
                        for(int i = 0; i < FWDpeopleLight.Length; i++)
                        {
                            FWDpeopleLight[i].DisableRed();
                            FWDpeopleLight[i].EnableGreen(false);
                        }

                        for(int i = 0; i < LRpeopleLight.Length; i++)
                        {
                            LRpeopleLight[i].DisableRed();
                            LRpeopleLight[i].EnableGreen(false);
                        }

                        for(int i = 0; i < FWDpeopleZebra.Length; i++)
                        {
                            FWDpeopleZebra[i].PEOPLE_CAN = true;
                        }

                        for(int i = 0; i < LRpeopleZebra.Length; i++)
                        {
                            LRpeopleZebra[i].PEOPLE_CAN = true;
                        }                                                

                        peopleTimer = peopleTime;
                        timePeople = true;
					}
                }
            }
        }
        else
        {
            if (timeBreak)
            {
                TimeBreak();
            }
        }

        if (greenTimer > 0)
        {
            greenTimer -= Time.deltaTime;

            if (greenTimer <= 0)
            {
				StartFlickerGreen();
            }
        }

        if(timePeople)
        {
            TimePeople();
        }
    }

	private void StartFlickerGreen()
	{
		if (blockForward)
		{
			for (int i = 0; i < LRlights.Length; i++)
			{
				LRlights[i].FlickerGreen(4.0f, 0.5f);
			}
		}
		else
		{
			for (int i = 0; i < FWDlights.Length; i++)
			{
				FWDlights[i].FlickerGreen(4.0f, 0.5f);
			}
		}
	}

    private void TimeBreak()
    {
        redTimer -= Time.deltaTime;

        if (redTimer <= 0)
        {
            if (blockForward)
            {
                if(FWDlights.Length > 0)
                {
                    blockForward = false;

                    for (int i = 0; i < FWDlights.Length; i++)
                    {
                        FWDlights[i].EnableYellow();
                    }
                }
                else
                {
                    for (int i = 0; i < LRlights.Length; i++)
                    {
                        LRlights[i].EnableYellow();
                    }
                }
            }
            else
            {
                if(LRlights.Length > 0)
                {
                    blockForward = true;

                    for (int i = 0; i < LRlights.Length; i++)
                    {
                        LRlights[i].EnableYellow();
                    }
                }
                else
                {
                    for (int i = 0; i < FWDlights.Length; i++)
                    {
                        FWDlights[i].EnableYellow();
                    }
                }
            }

            redTimer = redTime;
            yellowOn = true;
            Debug.Log("32");
        }
    }

    private void TimePeople()
    {
        peopleTimer -= Time.deltaTime;

        if(peopleTimer <= 0)
        {
            timePeople = false;

            for (int i = 0; i < FWDpeopleLight.Length; i++)
            {
                FWDpeopleLight[i].FlickerGreen(4.0f, 0.5f);
            }

            for (int i = 0; i < LRpeopleLight.Length; i++)
            {
                LRpeopleLight[i].FlickerGreen(4.0f, 0.5f);
            } 

            for(int i = 0; i < FWDpeopleZebra.Length; i++)
            {
                FWDpeopleZebra[i].PEOPLE_CAN = false;
            }

            for(int i = 0; i < LRpeopleZebra.Length; i++)
            {
                LRpeopleZebra[i].PEOPLE_CAN = false;
            }                           
        }
    }

    private void AllowFwd()
    {
        for (int i = 0; i < FWDlights.Length; i++)
        {
            FWDlights[i].EnableGreen(true);
            FWDlights[i].DisableRed();
            FWDlights[i].DisableYellow();
        }

        for (int i = 0; i < LRlights.Length; i++)
        {
            LRlights[i].DisableGreen(true);
            LRlights[i].DisableYellow();
            LRlights[i].EnableRed();
        }

        for (int i = 0; i < FWDpeopleZebra.Length; i++)
        {
            FWDpeopleZebra[i].CAR_CAN = true;
        }
    }

    private void AllowLR()
    {
        for (int i = 0; i < LRlights.Length; i++)
        {
            LRlights[i].EnableGreen(true);
            LRlights[i].DisableRed();
            LRlights[i].DisableYellow();
        }

        for (int i = 0; i < FWDlights.Length; i++)
        {
            FWDlights[i].DisableGreen(true);
            FWDlights[i].DisableYellow();
            FWDlights[i].EnableRed();
        }

        for (int i = 0; i < LRpeopleZebra.Length; i++)
        {
            LRpeopleZebra[i].CAR_CAN = true;
        }
    }	

    private void SetFlow()
    {
        if (blockForward)
        {
            AllowLR();
            return;
        }
        else
        {
            AllowFwd();
            return;
        }
    }

    public void ResetSemaphore()
    {
        timeBreak = true;
    }

    private void YellowTime()
    {
        if (blockForward)
        {
            for (int i = 0; i < LRpeopleZebra.Length; i++)
            {
                LRpeopleZebra[i].CAR_CAN = false;
            }
        }
        else
        {
            for (int i = 0; i < FWDpeopleZebra.Length; i++)
            {
                FWDpeopleZebra[i].CAR_CAN = false;
            }
        }
    }	

    private void OnTriggerStay(Collider other)
    {
        if (other.transform.CompareTag("Car"))
        {
            if (other.transform.GetComponentInParent<CarAIController>())
            {
                CarAIController car = other.GetComponentInParent<CarAIController>();
                car.INSIDE = true;
            }
        }

        if(other.transform.CompareTag("Bcycle"))
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
        if (other.transform.CompareTag("Car"))
        {
            if (other.transform.GetComponentInParent<CarAIController>())
            {
                CarAIController car = other.GetComponentInParent<CarAIController>();
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
    }					
}