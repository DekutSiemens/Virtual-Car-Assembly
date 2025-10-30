using System;
using realvirtual;

namespace VME.IO
{
    [Serializable]
    public class BoolIn
    {
        public PLCInputBool tag;
        public bool v, prev;
        public bool Rising => v && !prev;
        public bool Falling => !v && prev;
        public void Sample()
        {
            prev = v;
            v = (tag != null) && tag.Value;
        }
    }

    [Serializable]
    public class BoolOut
    {
        public PLCOutputBool tag;
        public void Set(bool x) { if (tag != null) tag.Value = x; }
        public bool Get() { return tag != null ? tag.Value : false; }
    }

    [Serializable]
    public class FloatIn
    {
        public PLCInputFloat tag;
        public float v, prev;
        public void Sample()
        {
            prev = v;
            v = (tag != null) ? tag.Value : 0f;
        }
    }

    [Serializable]
    public class FloatOut
    {
        public PLCOutputFloat tag;
        public void Set(float x) { if (tag != null) tag.Value = x; }
        public float Get() { return tag != null ? tag.Value : 0f; }
    }
}
