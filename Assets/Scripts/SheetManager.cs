using System.Collections.Generic;
using UnityEngine;

public class SheetManager : MonoBehaviour
{
    public GameObject[] sheets; // Array to hold the sheet GameObjects
    private int currentSheetIndex = 0;
    private int activatedSheetsCount = 1; // Start with the first sheet already activated

    void Start()
    {
        // Deactivate all sheets except the first one
        for (int i = 1; i < sheets.Length; i++)
        {
            sheets[i].SetActive(false);
        }

        // Ensure the first sheet is positioned correctly
        sheets[0].transform.position = transform.position;
    }

    void OnTriggerExit(Collider other)
    {
        if (other.CompareTag("Sheet") && other.gameObject == sheets[currentSheetIndex] && activatedSheetsCount < sheets.Length)
        {
            // Activate the next sheet
            ActivateNextSheet();
            activatedSheetsCount++;
        }
    }

    void ActivateNextSheet()
    {
        // Increment the index
        currentSheetIndex = (currentSheetIndex + 1) % sheets.Length;

        // Activate the next sheet
        sheets[currentSheetIndex].SetActive(true);

        // Position the newly activated sheet at the start of the conveyor
        sheets[currentSheetIndex].transform.position = transform.position;
        sheets[currentSheetIndex].transform.rotation = transform.rotation;
    }
}
