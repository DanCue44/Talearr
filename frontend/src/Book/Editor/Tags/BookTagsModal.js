import _ from 'lodash';
import PropTypes from 'prop-types';
import React from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import TagsModalContent from 'Author/Editor/Tags/TagsModalContent';
import Modal from 'Components/Modal/Modal';
import createTagsSelector from 'Store/Selectors/createTagsSelector';

// Talearr: bulk book tags for the book index editor. Reuses the author editor's tags modal;
// the current tags come from the Talearr book tags store.
function createMapStateToProps() {
  return createSelector(
    (state, { bookIds }) => bookIds,
    (state) => state.talearrBookTags.byBookId,
    createTagsSelector(),
    (bookIds, byBookId, tagList) => {
      return {
        authorTags: _.uniq(_.flatten(bookIds.map((id) => byBookId[id] || []))),
        tagList,
        howToApplyHelpText: 'How to apply tags to the selected books'
      };
    }
  );
}

const BookTagsModalContentConnector = connect(createMapStateToProps)(TagsModalContent);

function BookTagsModal(props) {
  const {
    isOpen,
    onModalClose,
    ...otherProps
  } = props;

  return (
    <Modal
      isOpen={isOpen}
      onModalClose={onModalClose}
    >
      <BookTagsModalContentConnector
        {...otherProps}
        onModalClose={onModalClose}
      />
    </Modal>
  );
}

BookTagsModal.propTypes = {
  isOpen: PropTypes.bool.isRequired,
  bookIds: PropTypes.arrayOf(PropTypes.number).isRequired,
  onApplyTagsPress: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default BookTagsModal;
