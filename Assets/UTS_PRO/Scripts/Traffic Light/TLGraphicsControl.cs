using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class TLGraphicsControl : MonoBehaviour
{
    private Material green, yellow, red, arrow;
    [SerializeField]private bool greenOn, yellowOn, redOn, arrowOn;
    private bool flickerGreen = false, flickerGreenCurrent = false;
    private float flickerTime, flickerRate, flickerCurrent;

    public TLGraphicsControl secondSide;
    public Texture2D greenG, yellowG, redG, arrowG;
    public MeshRenderer mshG, mshY, mshR, mshA;
    public bool twoSided;
    public bool hasArrow;

    private void Awake()
    {
        green = mshG.materials[1];

        if (mshY != null)
        {
            yellow = mshY.materials[1];
        }

        red = mshR.materials[1];

        if (hasArrow)
        {
            arrow = mshA.materials[1];
        }
    }

    private void Update()
    {
        if (flickerGreen)
        {
            flickerTime -= Time.deltaTime;

            if (flickerTime < 0)
            {
                flickerGreen = false;
                DisableGreen(hasArrow);

                SemaphoreSimulator SS = GetComponentInParent<SemaphoreSimulator>();

                if (mshY == null)
                {
                    EnableRed();

                    if(SS.STAGE == 1)
                    {
                        SS.ResetSemaphore();
                    }                    
                }
                else
                {
                    EnableYellow();

                    if (!SS.YELLOW_ON)
                    {
                        if(SS.STAGE == 0 || SS.STAGE == 1)
                        {
                            SS.YELLOW_ON = true;
                        }
                    }
                }
            }

            flickerCurrent -= Time.deltaTime;

            if (flickerCurrent < 0)
            {
                flickerCurrent = flickerRate;
                flickerGreenCurrent = !flickerGreenCurrent;

                if (flickerGreenCurrent)
                {
                    green.EnableKeyword("_EMISSION");

                    if (hasArrow)
                    {
                        arrow.EnableKeyword("_EMISSION");
                    }
                }
                else
                {
                    green.DisableKeyword("_EMISSION");

                    if (hasArrow)
                    {
                        arrow.DisableKeyword("_EMISSION");
                    }
                }
            }
        }
    }

    public void EnableGreen(bool withArrow)
    {
        if (!greenOn)
        {
            green.EnableKeyword("_EMISSION");
            green.SetTexture("_EmissionMap", greenG);
            greenOn = true;

            if (withArrow)
            {
                EnableArrow();
            }

            if (twoSided)
            {
                secondSide.EnableGreen(false);
            }
        }
    }

    public void EnableYellow()
    {
        if (!yellowOn)
        {
            yellow.EnableKeyword("_EMISSION");
            yellow.SetTexture("_EmissionMap", yellowG);
            yellowOn = true;

            if (twoSided)
            {
                secondSide.EnableYellow();
            }
        }
    }

    public void EnableRed()
    {
        if (!redOn)
        {
            red.EnableKeyword("_EMISSION");
            red.SetTexture("_EmissionMap", redG);
            redOn = true;

            if (twoSided)
            {
                secondSide.EnableRed();
            }
        }
    }

    public void EnableArrow()
    {
        if (!arrowOn && hasArrow)
        {
            arrow.EnableKeyword("_EMISSION");
            arrow.SetTexture("_EmissionMap", arrowG);
            arrowOn = true;

            if (twoSided)
            {
                secondSide.EnableArrow();
            }
        }
    }

    public void DisableGreen(bool withArrow)
    {
        if (greenOn)
        {
            green.DisableKeyword("_EMISSION");
            greenOn = false;

            if (withArrow && hasArrow)
            {
                DisableArrow();
            }

            if (twoSided)
            {
                secondSide.DisableGreen(false);
            }
        }
    }

    public void DisableYellow()
    {
        if (yellowOn)
        {
            yellow.DisableKeyword("_EMISSION");
            yellowOn = false;

            if (twoSided)
            {
                secondSide.DisableYellow();
            }
        }

    }

    public void DisableRed()
    {
        if (redOn)
        {
            red.DisableKeyword("_EMISSION");
            redOn = false;

            if (twoSided)
            {
                secondSide.DisableRed();
            }
        }
    }

    public void DisableArrow()
    {
        if (arrowOn)
        {
            arrow.DisableKeyword("_EMISSION");
            arrowOn = false;

            if (twoSided)
            {
                secondSide.DisableArrow();
            }
        }
    }

    public void FlickerGreen(float time, float rate)
    {
        if (twoSided)
        {
            secondSide.FlickerGreen(time, rate);
        }

        flickerTime = time;
        flickerRate = flickerCurrent = rate;
        flickerGreen = true;
    }
}