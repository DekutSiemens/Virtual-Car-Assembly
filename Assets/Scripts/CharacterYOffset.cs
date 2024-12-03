using UnityEngine;

public class CharacterYOffset : MonoBehaviour
{
    private void LateUpdate()
    {
        var parent = transform.parent;
        var yOffset = parent.localPosition.y;

        var floorElevationOffset = parent.GetComponent<CharacterController>().bounds.min.y;
        transform.localPosition = new Vector3(transform.localPosition.x, floorElevationOffset - yOffset, transform.localPosition.z);
    }
}
