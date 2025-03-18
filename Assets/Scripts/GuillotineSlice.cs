using UnityEngine;
using EzySlice;


public class GuillotineSlice : MonoBehaviour
{
    public Transform startSlicePoint;
    public Transform endSlicePoint;
    public VelocityEstimator velocityEstimator;
    public Material crossSectionMaterial;
    public LayerMask sliceableLayer;
    public float cutForce = 2000;

    void FixedUpdate()
    {
        bool hasHit = Physics.Linecast(startSlicePoint.position, endSlicePoint.position, out RaycastHit hit, sliceableLayer);
        if (hasHit)
        {
            GameObject target = hit.transform.gameObject;
            Slice(target);
        }
    }

    public void Slice(GameObject target)
    {
        Vector3 velocity = velocityEstimator.GetVelocityEstimate();
        Vector3 planeNormal = Vector3.Cross(endSlicePoint.position - startSlicePoint.position, velocity);
        planeNormal.Normalize();

        SlicedHull hull = target.Slice(endSlicePoint.position, planeNormal);
        if (hull != null)
        {
            GameObject upperHull = hull.CreateUpperHull(target, crossSectionMaterial);
            SetupSlicedObject(upperHull, target, true);

            GameObject lowerHull = hull.CreateLowerHull(target, crossSectionMaterial);
            SetupSlicedObject(lowerHull, target, false);

            // Deactivate the original object instead of destroying it
            target.SetActive(false);
        }
    }
    public void SetupSlicedObject(GameObject slicedObject, GameObject originalObject, bool isUpperHull)
    {
        // Preserve the original object's position and rotation
        slicedObject.transform.position = originalObject.transform.position;
        slicedObject.transform.rotation = originalObject.transform.rotation;

        Rigidbody rb = slicedObject.AddComponent<Rigidbody>();
        MeshCollider collider = slicedObject.AddComponent<MeshCollider>();
        collider.convex = true;


        rb.AddExplosionForce(cutForce, slicedObject.transform.position, 1);
        rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
        rb.mass= 5;

        slicedObject.tag = "Sheet";
        slicedObject.layer = LayerMask.NameToLayer("sliceable");

        UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable grabInteractable = slicedObject.AddComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable>();
        grabInteractable.movementType = UnityEngine.XR.Interaction.Toolkit.Interactables.XRBaseInteractable.MovementType.VelocityTracking;
        grabInteractable.useDynamicAttach = true;
        grabInteractable.throwOnDetach = true;
        grabInteractable.smoothPosition = true;
        grabInteractable.smoothRotation = true;
        grabInteractable.smoothScale = true;

        // Create a consistent attach point
        CreateConsistentAttachTransform(slicedObject, isUpperHull);
    }

    private void CreateConsistentAttachTransform(GameObject slicedObject, bool isUpperHull)
    {
        GameObject attachPoint = new GameObject("AttachPoint");
        attachPoint.transform.SetParent(slicedObject.transform, false);

        // Get the mesh bounds
        Mesh mesh = slicedObject.GetComponent<MeshFilter>().mesh;
        mesh.RecalculateBounds();
        Bounds meshBounds = mesh.bounds;

        // Set the attach point to the center of the sliced object
        attachPoint.transform.localPosition = meshBounds.center;

        // Adjust the attach point slightly towards the cut face
        Vector3 adjustment = isUpperHull ? Vector3.down : Vector3.up;
        attachPoint.transform.localPosition += adjustment * (meshBounds.extents.y * 0.5f);

        UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable grabInteractable = slicedObject.GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable>();
        grabInteractable.attachTransform = attachPoint.transform;
    }
}