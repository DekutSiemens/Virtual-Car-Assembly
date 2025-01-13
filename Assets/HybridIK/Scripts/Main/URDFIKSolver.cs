using UnityEngine;
using System.Collections.Generic;
using System.Xml;
using System;

public enum URDFJointType
{
    REVOLUTE,
    PRISMATIC,
    FIXED,
    CONTINUOUS,
    FLOATING,
    PLANAR
}

[Serializable]
public class URDFJoint
{
    public string name;
    public URDFJointType type;
    public Vector3 axis = Vector3.up;
    public Vector3 origin;
    public Vector3 originRPY;
    public float lowerLimit;
    public float upperLimit;
    public float effortLimit;
    public float velocityLimit;
    public string parentLink;
    public string childLink;
}

[Serializable]
public class URDFLink
{
    public string name;
    public Vector3 inertialOrigin;
    public float mass;
    public Vector3 inertia;
    public List<URDFJoint> childJoints = new List<URDFJoint>();
}

public class URDFIKSolver : MonoBehaviour
{
    [Header("URDF Configuration")]
    public TextAsset urdfFile;
    public Transform rootNode;
    public Transform targetTransform;

    [Header("IK Settings")]
    public int maxIterations = 10;
    public float convergenceThreshold = 0.001f;
    public bool enforceJointLimits = true;
    public float dampingFactor = 0.1f;

    private Dictionary<string, URDFLink> links = new Dictionary<string, URDFLink>();
    private Dictionary<string, URDFJoint> joints = new Dictionary<string, URDFJoint>();
    private List<Transform> chainTransforms = new List<Transform>();
    private Matrix4x4[] jacobian;
    private Vector3 lastTargetPosition;
    private bool isInitialized = false;

    void Start()
    {
        if (urdfFile != null)
        {
            ParseURDF();
            InitializeChain();
        }
    }

    void ParseURDF()
    {
        XmlDocument doc = new XmlDocument();
        doc.LoadXml(urdfFile.text);

        // Parse Links
        XmlNodeList linkNodes = doc.SelectNodes("//link");
        foreach (XmlNode linkNode in linkNodes)
        {
            URDFLink link = new URDFLink();
            link.name = linkNode.Attributes["name"].Value;

            // Parse inertial data
            XmlNode inertialNode = linkNode.SelectSingleNode("inertial");
            if (inertialNode != null)
            {
                XmlNode originNode = inertialNode.SelectSingleNode("origin");
                if (originNode != null)
                {
                    string[] xyz = originNode.Attributes["xyz"]?.Value.Split(' ');
                    if (xyz.Length == 3)
                    {
                        link.inertialOrigin = new Vector3(
                            float.Parse(xyz[0]),
                            float.Parse(xyz[1]),
                            float.Parse(xyz[2])
                        );
                    }
                }

                XmlNode massNode = inertialNode.SelectSingleNode("mass");
                if (massNode != null)
                {
                    link.mass = float.Parse(massNode.Attributes["value"].Value);
                }
            }

            links[link.name] = link;
        }

        // Parse Joints
        XmlNodeList jointNodes = doc.SelectNodes("//joint");
        foreach (XmlNode jointNode in jointNodes)
        {
            URDFJoint joint = new URDFJoint();
            joint.name = jointNode.Attributes["name"].Value;
            joint.type = (URDFJointType)Enum.Parse(typeof(URDFJointType),
                                                  jointNode.Attributes["type"].Value.ToUpper());

            // Parse joint limits
            XmlNode limitsNode = jointNode.SelectSingleNode("limit");
            if (limitsNode != null)
            {
                joint.lowerLimit = float.Parse(limitsNode.Attributes["lower"]?.Value ?? "0");
                joint.upperLimit = float.Parse(limitsNode.Attributes["upper"]?.Value ?? "0");
                joint.effortLimit = float.Parse(limitsNode.Attributes["effort"]?.Value ?? "0");
                joint.velocityLimit = float.Parse(limitsNode.Attributes["velocity"]?.Value ?? "0");
            }

            // Parse joint axis
            XmlNode axisNode = jointNode.SelectSingleNode("axis");
            if (axisNode != null)
            {
                string[] xyz = axisNode.Attributes["xyz"].Value.Split(' ');
                joint.axis = new Vector3(
                    float.Parse(xyz[0]),
                    float.Parse(xyz[1]),
                    float.Parse(xyz[2])
                );
            }

            // Parse parent and child links
            XmlNode parentNode = jointNode.SelectSingleNode("parent");
            XmlNode childNode = jointNode.SelectSingleNode("child");
            joint.parentLink = parentNode.Attributes["link"].Value;
            joint.childLink = childNode.Attributes["link"].Value;

            joints[joint.name] = joint;
            links[joint.parentLink].childJoints.Add(joint);
        }
    }

    void InitializeChain()
    {
        chainTransforms.Clear();
        Transform current = rootNode;

        while (current != null)
        {
            chainTransforms.Add(current);
            if (current.childCount > 0)
                current = current.GetChild(0);
            else
                break;
        }

        jacobian = new Matrix4x4[chainTransforms.Count];
        isInitialized = true;
    }

    void CalculateJacobian()
    {
        Vector3 endEffectorPosition = chainTransforms[chainTransforms.Count - 1].position;

        for (int i = 0; i < chainTransforms.Count; i++)
        {
            Transform joint = chainTransforms[i];
            URDFJoint urdfJoint = GetJointFromTransform(joint);

            if (urdfJoint == null || urdfJoint.type == URDFJointType.FIXED)
            {
                jacobian[i] = Matrix4x4.zero;
                continue;
            }

            Vector3 jointPosition = joint.position;
            Vector3 jointAxis = joint.TransformDirection(urdfJoint.axis);

            if (urdfJoint.type == URDFJointType.REVOLUTE || urdfJoint.type == URDFJointType.CONTINUOUS)
            {
                Vector3 crossProduct = Vector3.Cross(jointAxis, endEffectorPosition - jointPosition);
                jacobian[i] = MatrixFromVector(crossProduct);
            }
            else if (urdfJoint.type == URDFJointType.PRISMATIC)
            {
                jacobian[i] = MatrixFromVector(jointAxis);
            }
        }
    }

    Matrix4x4 MatrixFromVector(Vector3 vec)
    {
        Matrix4x4 matrix = Matrix4x4.zero;
        matrix.SetColumn(0, new Vector4(vec.x, 0, 0, 0));
        matrix.SetColumn(1, new Vector4(0, vec.y, 0, 0));
        matrix.SetColumn(2, new Vector4(0, 0, vec.z, 0));
        return matrix;
    }

    URDFJoint GetJointFromTransform(Transform transform)
    {
        foreach (var joint in joints.Values)
        {
            if (transform.name.Contains(joint.name))
                return joint;
        }
        return null;
    }

    void ApplyJointLimits(Transform joint, ref Vector3 rotation)
    {
        URDFJoint urdfJoint = GetJointFromTransform(joint);
        if (urdfJoint == null || !enforceJointLimits) return;

        if (urdfJoint.type == URDFJointType.REVOLUTE)
        {
            float angle = Vector3.Dot(rotation, urdfJoint.axis);
            angle = Mathf.Clamp(angle, urdfJoint.lowerLimit, urdfJoint.upperLimit);
            rotation = urdfJoint.axis * angle;
        }
        else if (urdfJoint.type == URDFJointType.PRISMATIC)
        {
            float translation = Vector3.Dot(rotation, urdfJoint.axis);
            translation = Mathf.Clamp(translation, urdfJoint.lowerLimit, urdfJoint.upperLimit);
            rotation = urdfJoint.axis * translation;
        }
    }

    void SolveIK()
    {
        if (!isInitialized) return;

        Vector3 targetPosition = targetTransform.position;
        if ((targetPosition - lastTargetPosition).magnitude < convergenceThreshold)
            return;

        for (int iteration = 0; iteration < maxIterations; iteration++)
        {
            CalculateJacobian();

            Vector3 endEffectorPosition = chainTransforms[chainTransforms.Count - 1].position;
            Vector3 positionError = targetPosition - endEffectorPosition;

            if (positionError.magnitude < convergenceThreshold)
                break;

            // Damped Least Squares method
            Matrix4x4 dampedJacobian = CalculateDampedPseudoInverse();
            Vector3 jointCorrection = MultiplyJacobianWithError(dampedJacobian, positionError);

            // Apply corrections to joints
            for (int i = 0; i < chainTransforms.Count; i++)
            {
                Transform joint = chainTransforms[i];
                URDFJoint urdfJoint = GetJointFromTransform(joint);

                if (urdfJoint == null || urdfJoint.type == URDFJointType.FIXED)
                    continue;

                Vector3 correction = new Vector3(
                    jointCorrection[i * 3],
                    jointCorrection[i * 3 + 1],
                    jointCorrection[i * 3 + 2]
                );

                ApplyJointLimits(joint, ref correction);

                if (urdfJoint.type == URDFJointType.REVOLUTE || urdfJoint.type == URDFJointType.CONTINUOUS)
                {
                    joint.Rotate(correction * Mathf.Rad2Deg);
                }
                else if (urdfJoint.type == URDFJointType.PRISMATIC)
                {
                    joint.Translate(correction);
                }
            }
        }

        lastTargetPosition = targetPosition;
    }

    Matrix4x4 CalculateDampedPseudoInverse()
    {
        // Simplified damped least squares implementation
        Matrix4x4 jTranspose = TransposeJacobian();
        Matrix4x4 jjt = MultiplyJacobianWithTranspose(jacobian, jTranspose);

        // Add damping
        for (int i = 0; i < 4; i++)
        {
            jjt[i, i] += dampingFactor * dampingFactor;
        }

        return MultiplyMatrices(jTranspose, InverseMatrix4x4(jjt));
    }

    Vector3 MultiplyJacobianWithError(Matrix4x4 jacobian, Vector3 error)
    {
        Vector3 result = Vector3.zero;
        for (int i = 0; i < 3; i++)
        {
            result[i] = Vector4.Dot(jacobian.GetRow(i), new Vector4(error.x, error.y, error.z, 1));
        }
        return result;
    }

    Matrix4x4 TransposeJacobian()
    {
        Matrix4x4 transposed = Matrix4x4.zero;
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                transposed[i, j] = jacobian[j, i];
        return transposed;
    }

    Matrix4x4 MultiplyJacobianWithTranspose(Matrix4x4[] j, Matrix4x4 jt)
    {
        Matrix4x4 result = Matrix4x4.zero;
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                for (int k = 0; k < 4; k++)
                    result[i, j] += j[i][k] * jt[k, j];
        return result;
    }

    Matrix4x4 MultiplyMatrices(Matrix4x4 a, Matrix4x4 b)
    {
        Matrix4x4 result = Matrix4x4.zero;
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                for (int k = 0; k < 4; k++)
                    result[i, j] += a[i, k] * b[k, j];
        return result;
    }

    Matrix4x4 InverseMatrix4x4(Matrix4x4 m)
    {
        return Matrix4x4.Inverse(m);
    }

    void LateUpdate()
    {
        SolveIK();
    }

    void OnDrawGizmos()
    {
        if (!isInitialized) return;

        // Draw chain
        Gizmos.color = Color.green;
        for (int i = 0; i < chainTransforms.Count - 1; i++)
        {
            Gizmos.DrawLine(chainTransforms[i].position, chainTransforms[i + 1].position);
        }

        // Draw target
        if (targetTransform != null)
        {
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(targetTransform.position, 0.1f);
        }
    }
}