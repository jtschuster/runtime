using System;

namespace SharedTypes.ComInterfaces
{
    interface IGetAndSetInt
    {
        public int GetData();
        public void SetData(int x);
        public static Guid IID = new Guid("2c3f9903-b586-46b1-881b-adfce9af47b1");
    }
}
