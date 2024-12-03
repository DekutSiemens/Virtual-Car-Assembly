using UnityEngine;
using System.Collections;

public class MoveThroughPoints : MonoBehaviour
{
    public Transform[] points; // Array to store the points (empty GameObjects)
    public float speed = 5f; // Speed of the movement
    public bool shouldMove = false; // Bool condition to control the movement

    private int currentPointIndex = 0; // Index of the current point

    void Update()
    {
        if (shouldMove)
        {
            shouldMove = false; // Reset the boolean to false
            if (points.Length > 0)
            {
                StartCoroutine(MoveToNextPoint());
            }
        }
    }

    IEnumerator MoveToNextPoint()
    {
        for (int i = 0; i < points.Length; i++)
        {
            Vector3 targetPosition = points[currentPointIndex].position;

            while (Vector3.Distance(transform.position, targetPosition) > 0.1f)
            {
                transform.position = Vector3.MoveTowards(transform.position, targetPosition, speed * Time.deltaTime);
                yield return null;
            }

            yield return new WaitForSeconds(0.5f);

            currentPointIndex = (currentPointIndex + 1) % points.Length;
        }
    }
}
