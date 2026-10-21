import PropTypes from 'prop-types';
import React from 'react';
import AuthorImage from 'Author/AuthorImage';

// Talearr: books without a cover show the dimmed Talearr logo (the title is drawn over it).
const coverPlaceholder = '/Content/Images/book-placeholder.png';

function BookCover(props) {
  return (
    <AuthorImage
      {...props}
      coverType="cover"
      placeholder={coverPlaceholder}
      usePlaceholderOnError={true}
    />
  );
}

BookCover.propTypes = {
  size: PropTypes.number.isRequired
};

BookCover.defaultProps = {
  size: 250
};

export default BookCover;
