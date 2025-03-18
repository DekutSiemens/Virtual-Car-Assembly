using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ShapeShift : MonoBehaviour
{
    public List<GameObject> aluminiumPart;

    void Start()
    {
        foreach (var item in aluminiumPart)
        {
            item.SetActive(false);
        }
    }

    public void Break()
    {
        foreach (var item in aluminiumPart)
        {
            if (item != null)
            {
                item.SetActive(true);
                item.transform.parent = null;
            }
        }
        gameObject.SetActive(false);
    }
}
