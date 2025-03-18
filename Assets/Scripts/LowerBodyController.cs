using UnityEngine;

public class LowerBodyController : MonoBehaviour
{
    [SerializeField] private Animator animator;
    [SerializeField][Range(0, 1)] private float leftFootPositionweight;
    [SerializeField][Range(0, 1)] private float rightFootPositionweight;

    [SerializeField][Range(0, 1)] private float leftFootRoratationWeight;
    [SerializeField][Range(0, 1)] private float rightFootRoratationWeight;

    [SerializeField] private Vector3 footOffset;

    [SerializeField] private Vector3 raycastLeftOffset;
    [SerializeField] private Vector3 raycastRightOffset;

    private void OnAnimatorIK(int layerIndex)
    {
        Vector3 leftFootPosition = animator.GetIKPosition(AvatarIKGoal.LeftFoot);
        Vector3 rightFootPosition = animator.GetIKPosition(AvatarIKGoal.RightFoot);

        RaycastHit hitleftFoot;
        RaycastHit hitrightFoot;

        bool isleftFootDown = Physics.Raycast(leftFootPosition + raycastLeftOffset, Vector3.down, out hitleftFoot);
        bool isrightFootDown = Physics.Raycast(rightFootPosition + raycastRightOffset, Vector3.down, out hitrightFoot);
        CalculateLeftFoot(isleftFootDown, hitleftFoot);
        CalculateRightFoot(isrightFootDown, hitrightFoot);

    }

    void CalculateLeftFoot(bool isleftFootDown, RaycastHit hitleftFoot)
    {
        if (isleftFootDown)
        {
            animator.SetIKPositionWeight(AvatarIKGoal.LeftFoot, leftFootPositionweight);
            animator.SetIKPosition(AvatarIKGoal.LeftFoot, hitleftFoot.point + footOffset);

            Quaternion leftFootRotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(transform.forward, hitleftFoot.normal), hitleftFoot.normal);
            animator.SetIKRotationWeight(AvatarIKGoal.LeftFoot, leftFootRoratationWeight);
            animator.SetIKRotation(AvatarIKGoal.LeftFoot, leftFootRotation);
        }
        else
        {
            animator.SetIKPositionWeight(AvatarIKGoal.LeftFoot, 0);
        }
    }
    void CalculateRightFoot(bool isrightFootDown, RaycastHit hitrightFoot)
    {
        if (isrightFootDown)
        {
            animator.SetIKPositionWeight(AvatarIKGoal.RightFoot, rightFootPositionweight);
            animator.SetIKPosition(AvatarIKGoal.RightFoot, hitrightFoot.point + footOffset);

            Quaternion rightFootRotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(transform.forward, hitrightFoot.normal), hitrightFoot.normal);
            animator.SetIKRotationWeight(AvatarIKGoal.RightFoot, rightFootRoratationWeight);
            animator.SetIKRotation(AvatarIKGoal.RightFoot, rightFootRotation);
        }
        else
        {
           animator.SetIKPositionWeight(AvatarIKGoal.RightFoot, 0);
        }
    }
}
