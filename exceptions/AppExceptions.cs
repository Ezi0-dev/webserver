public class DuplicateEmailException : Exception
{
    public DuplicateEmailException() : base("Email already in use") { }
}